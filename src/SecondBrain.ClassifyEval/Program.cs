// Throwaway harness: measure where Haiku 4.5's batched-classification accuracy
// falls off as the input payload grows, and (once locked) bulk-fill the
// source_type column for every NULL row in fts.db. Three subcommands:
//
//   1. prep — pulls unclassified summaries from fts.db, packs them into 15
//      disjoint batches (5 each at ~32 / ~64 / ~128 KB), writes JSON files.
//      A separate process labels each batch (truth_label per item) and writes
//      it back into the same files.
//
//   2. run — for each batch file, builds a single API call to Haiku with all
//      summaries delimited, parses the response, scores against truth_label.
//      Prints per-batch and per-size accuracy.
//
//   3. backfill — one-shot bulk reclassify of every fts.db row where
//      source_type IS NULL. Reads existing summaries (no re-summarization),
//      packs at 32 KB, calls Haiku/low with the locked prompt, writes
//      predictions back to files.source_type. Resumable: re-running picks up
//      whatever rows remain NULL.
//
// Throwaway. Wipe `src/SecondBrain.ClassifyEval/` and the solution entry to remove.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Data.Sqlite;
using SecondBrain.Llm;

var defaultClassifierModel = "claude-haiku-4-5";
var canonicalTypes = new[] { "transcript", "standup", "1on1", "planning", "note" };

if (args.Length == 0) { PrintUsage(); return 1; }

return args[0] switch
{
    "prep" => await PrepAsync(args[1..]),
    "run" => await RunAsync(args[1..]),
    "backfill" => await BackfillAsync(args[1..]),
    _ => Fail($"unknown command: {args[0]}"),
};

async Task<int> PrepAsync(string[] cmdArgs)
{
    var dbPath = ArgValue(cmdArgs, "--db") ?? DefaultDbPath();
    var outDir = ArgValue(cmdArgs, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "classify-eval-batches");
    Directory.CreateDirectory(outDir);

    Console.WriteLine($"db   : {dbPath}");
    Console.WriteLine($"out  : {outDir}");

    var rows = LoadUnclassifiedSummaries(dbPath);
    Console.WriteLine($"loaded {rows.Count} unclassified summaries");

    // Greedily pack disjoint batches: 5 at 32 KB, 5 at 64 KB, 5 at 128 KB, in that
    // order so the smaller batches get filled first (less risk of starvation).
    var pool = new Queue<(long Id, string Summary)>(rows.OrderBy(r => Random.Shared.Next()));
    var sizesKb = new[] { 32, 32, 32, 32, 32, 64, 64, 64, 64, 64, 128, 128, 128, 128, 128 };
    var batchIndex = 0;
    var perSizeCounter = new Dictionary<int, int>();

    foreach (var sizeKb in sizesKb)
    {
        var sizeCap = sizeKb * 1024;
        var items = new List<BatchItem>();
        var bytesUsed = 0;
        while (pool.Count > 0)
        {
            var next = pool.Peek();
            // approximate: encoded bytes of the summary plus a small overhead per item
            var itemBytes = Encoding.UTF8.GetByteCount(next.Summary) + 80;
            if (items.Count > 0 && bytesUsed + itemBytes > sizeCap) break;
            pool.Dequeue();
            items.Add(new BatchItem(next.Id, next.Summary, null));
            bytesUsed += itemBytes;
            if (bytesUsed >= sizeCap) break;
        }

        perSizeCounter.TryGetValue(sizeKb, out var n);
        perSizeCounter[sizeKb] = n + 1;

        var file = new BatchFile(
            BatchId: $"{sizeKb}kb-{n + 1}",
            TargetSizeKb: sizeKb,
            ApproximateBytes: bytesUsed,
            Items: items);
        var path = Path.Combine(outDir, $"batch_{sizeKb}kb_{n + 1}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(file, Shared.JsonOpts));
        Console.WriteLine($"  wrote {Path.GetFileName(path)}: {items.Count} items, {bytesUsed:N0} bytes");
        batchIndex++;
    }

    if (pool.Count > 0)
        Console.WriteLine($"({pool.Count} unused summaries remain in the pool)");

    return 0;
}

async Task<int> RunAsync(string[] cmdArgs)
{
    var inDir = ArgValue(cmdArgs, "--in") ?? Path.Combine(Directory.GetCurrentDirectory(), "classify-eval-batches");
    if (!Directory.Exists(inDir)) return Fail($"input dir not found: {inDir}");

    var classifierModel = ArgValue(cmdArgs, "--model") ?? defaultClassifierModel;
    var effortStr = ArgValue(cmdArgs, "--effort") ?? "low";
    Console.WriteLine($"model: {classifierModel}  effort: {effortStr}\n");
    var configPath = ArgValue(cmdArgs, "--config") ?? DefaultConfigPath();
    var (apiKey, vertexBaseUrl) = ReadApiCreds(configPath);
    if (string.IsNullOrEmpty(apiKey)
        && Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX") != "1")
        return Fail("API key not set and CLAUDE_CODE_USE_VERTEX != 1");

    var rawClient = ClaudeSessionFactory.BuildClient(apiKey, vertexBaseUrl);
    var client = new AnthropicMessageCreator(rawClient);

    var batchFiles = Directory.GetFiles(inDir, "batch_*.json").OrderBy(p => p).ToList();
    Console.WriteLine($"found {batchFiles.Count} batch files in {inDir}\n");

    var resultsBySize = new Dictionary<int, List<BatchResult>>();
    foreach (var file in batchFiles)
    {
        var batch = JsonSerializer.Deserialize<BatchFile>(File.ReadAllText(file), Shared.JsonOpts)
            ?? throw new InvalidDataException($"failed to parse {file}");

        // Merge in truth labels from a sibling labels file if present. Format:
        // labels_<batchname>.json containing { "labels": [{"id": <int>, "label": "<str>"}, ...] }.
        // Agents write to this file rather than mutating the batch JSON in-place
        // (which corrupts embedded escape sequences during string round-trip).
        var labelsPath = Path.Combine(inDir, $"labels_{Path.GetFileNameWithoutExtension(file)}.json");
        if (File.Exists(labelsPath))
        {
            try
            {
                var labelsFile = JsonSerializer.Deserialize<LabelsFile>(File.ReadAllText(labelsPath), Shared.JsonOpts);
                if (labelsFile?.Labels != null)
                {
                    var byId = labelsFile.Labels.ToDictionary(l => l.Id, l => l.Label);
                    batch = batch with
                    {
                        Items = batch.Items
                            .Select(i => byId.TryGetValue(i.Id, out var lbl) ? i with { TruthLabel = lbl } : i)
                            .ToList(),
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] {batch.BatchId}: failed to load {Path.GetFileName(labelsPath)}: {ex.Message}");
            }
        }

        var unlabeled = batch.Items.Count(i => string.IsNullOrEmpty(i.TruthLabel));
        if (unlabeled > 0)
        {
            Console.WriteLine($"[skip] {batch.BatchId}: {unlabeled}/{batch.Items.Count} items lack truth_label");
            continue;
        }

        var result = await ScoreBatchAsync(client, classifierModel, effortStr, batch);
        if (!resultsBySize.TryGetValue(batch.TargetSizeKb, out var list))
            resultsBySize[batch.TargetSizeKb] = list = new List<BatchResult>();
        list.Add(result);

        Console.WriteLine($"[{batch.BatchId}] items={batch.Items.Count} payload≈{batch.ApproximateBytes:N0}B accuracy={result.Accuracy:P1} (correct {result.Correct}/{result.Total}, missing {result.MissingLabels}, invalid {result.InvalidLabels})");
        if (result.Confusion.Count > 0)
        {
            foreach (var ((truth, pred), count) in result.Confusion.OrderByDescending(kv => kv.Value).Take(5))
                Console.WriteLine($"    {truth,-12} → {pred,-12}  {count}x");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== aggregate by size ===");
    foreach (var (sizeKb, list) in resultsBySize.OrderBy(kv => kv.Key))
    {
        var totalCorrect = list.Sum(r => r.Correct);
        var totalCount = list.Sum(r => r.Total);
        var meanAcc = totalCount > 0 ? (double)totalCorrect / totalCount : 0;
        Console.WriteLine($"  {sizeKb,3} KB  : {meanAcc:P1}  ({totalCorrect}/{totalCount} across {list.Count} batches)");
    }

    return 0;
}

async Task<int> BackfillAsync(string[] cmdArgs)
{
    var dbPath = ArgValue(cmdArgs, "--db") ?? DefaultDbPath();
    var configPath = ArgValue(cmdArgs, "--config") ?? DefaultConfigPath();
    var classifierModel = ArgValue(cmdArgs, "--model") ?? defaultClassifierModel;
    var effortStr = ArgValue(cmdArgs, "--effort") ?? "low";
    var batchKb = int.TryParse(ArgValue(cmdArgs, "--batch-kb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bk) ? bk : 32;
    var maxBatches = int.TryParse(ArgValue(cmdArgs, "--max-batches"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : int.MaxValue;
    var dryRun = cmdArgs.Contains("--dry-run");

    Console.WriteLine($"db        : {dbPath}");
    Console.WriteLine($"model     : {classifierModel}  effort: {effortStr}  batch: {batchKb} KB");
    Console.WriteLine($"dry-run   : {dryRun}");
    Console.WriteLine();

    if (!File.Exists(dbPath)) return Fail($"db not found: {dbPath}");

    var (apiKey, vertexBaseUrl) = ReadApiCreds(configPath);
    if (string.IsNullOrEmpty(apiKey)
        && Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX") != "1")
        return Fail("API key not set and CLAUDE_CODE_USE_VERTEX != 1");

    var rows = LoadUnclassifiedSummaries(dbPath);
    Console.WriteLine($"loaded {rows.Count:N0} rows with NULL source_type and a non-empty summary");
    if (rows.Count == 0) { Console.WriteLine("nothing to do."); return 0; }

    // Pack into ~batchKb-sized batches using the same greedy algorithm as the eval prep.
    // No randomization — deterministic order so re-runs after partial completion produce
    // the same packing structure for whatever's left NULL.
    var pool = new Queue<(long Id, string Summary)>(rows);
    var batches = new List<List<(long Id, string Summary)>>();
    var sizeCap = batchKb * 1024;
    while (pool.Count > 0)
    {
        var items = new List<(long Id, string Summary)>();
        var bytesUsed = 0;
        while (pool.Count > 0)
        {
            var next = pool.Peek();
            var itemBytes = Encoding.UTF8.GetByteCount(next.Summary) + 80;
            if (items.Count > 0 && bytesUsed + itemBytes > sizeCap) break;
            pool.Dequeue();
            items.Add(next);
            bytesUsed += itemBytes;
            if (bytesUsed >= sizeCap) break;
        }
        batches.Add(items);
    }
    Console.WriteLine($"packed into {batches.Count:N0} batches at ≤{batchKb} KB each");
    if (maxBatches < batches.Count)
    {
        Console.WriteLine($"capped at first {maxBatches} batches via --max-batches");
        batches = batches.Take(maxBatches).ToList();
    }
    Console.WriteLine();

    var rawClient = ClaudeSessionFactory.BuildClient(apiKey, vertexBaseUrl);
    var client = new AnthropicMessageCreator(rawClient);
    var systemPrompt = GetClassifierSystemPrompt();
    var (thinking, maxTokens) = EffortConfig.Resolve(effortStr, baseOutputTokens: 8_192);

    SqliteConnection? writeConn = null;
    SqliteCommand? updateCmd = null;
    if (!dryRun)
    {
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString();
        writeConn = new SqliteConnection(connStr);
        writeConn.Open();
        updateCmd = writeConn.CreateCommand();
        // The `AND source_type IS NULL` guard makes the UPDATE idempotent — if another
        // writer somehow filled the row first, we don't clobber its label.
        updateCmd.CommandText = "UPDATE files SET source_type = @t WHERE id = @id AND source_type IS NULL";
        updateCmd.Parameters.Add("@t", SqliteType.Text);
        updateCmd.Parameters.Add("@id", SqliteType.Integer);
    }

    var totalUpdated = 0;
    var totalSkipped = 0;
    var totalInvalid = 0;
    var totalMissing = 0;
    var perTypeCounts = new Dictionary<string, int>();
    for (var b = 0; b < batches.Count; b++)
    {
        var items = batches[b];
        var sb = new StringBuilder();
        var seqToId = new Dictionary<int, long>();
        for (var i = 0; i < items.Count; i++)
        {
            var seq = i + 1;
            seqToId[seq] = items[i].Id;
            sb.AppendLine($"=====BEGIN:{seq}=====");
            sb.AppendLine(items[i].Summary);
            sb.AppendLine($"=====END:{seq}=====");
            sb.AppendLine();
        }

        var createParams = new MessageCreateParams
        {
            Model = classifierModel,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = [new MessageParam { Role = Role.User, Content = sb.ToString() }],
        };
        if (thinking != null) createParams = createParams with { Thinking = thinking };

        var response = await client.CreateAsync(createParams, CancellationToken.None);
        var responseText = response.Content
            .Where(block => block.TryPickText(out _))
            .Select(block => { block.TryPickText(out var t); return t!.Text; })
            .FirstOrDefault() ?? "";

        var labelPattern = new Regex(@"={5}LABEL:(\d+)=([^=\s]+)={5}");
        var predictions = new Dictionary<int, string>();
        foreach (Match m in labelPattern.Matches(responseText))
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
            predictions[seq] = m.Groups[2].Value.Trim();
        }

        var batchUpdated = 0;
        var batchInvalid = 0;
        var batchMissing = 0;
        var batchSkipped = 0;
        if (writeConn != null && updateCmd != null)
        {
            using var txn = writeConn.BeginTransaction();
            updateCmd.Transaction = txn;
            foreach (var (seq, id) in seqToId)
            {
                if (!predictions.TryGetValue(seq, out var pred)) { batchMissing++; continue; }
                if (!canonicalTypes.Contains(pred)) { batchInvalid++; continue; }
                updateCmd.Parameters["@t"].Value = pred;
                updateCmd.Parameters["@id"].Value = id;
                var n = updateCmd.ExecuteNonQuery();
                if (n > 0)
                {
                    batchUpdated++;
                    perTypeCounts.TryGetValue(pred, out var c);
                    perTypeCounts[pred] = c + 1;
                }
                else
                {
                    batchSkipped++; // row already had a non-NULL source_type when we tried to write
                }
            }
            txn.Commit();
        }
        else
        {
            // dry run — just count what would happen
            foreach (var (seq, _) in seqToId)
            {
                if (!predictions.TryGetValue(seq, out var pred)) { batchMissing++; continue; }
                if (!canonicalTypes.Contains(pred)) { batchInvalid++; continue; }
                batchUpdated++;
                perTypeCounts.TryGetValue(pred, out var c);
                perTypeCounts[pred] = c + 1;
            }
        }

        totalUpdated += batchUpdated;
        totalInvalid += batchInvalid;
        totalMissing += batchMissing;
        totalSkipped += batchSkipped;

        Console.WriteLine($"  [{b + 1,4}/{batches.Count}] items={items.Count,3} updated={batchUpdated,3} missing={batchMissing} invalid={batchInvalid} skipped={batchSkipped}");
    }

    writeConn?.Dispose();

    Console.WriteLine();
    Console.WriteLine("=== summary ===");
    Console.WriteLine($"  rows examined : {rows.Count:N0}");
    Console.WriteLine($"  updated       : {totalUpdated:N0}{(dryRun ? "  (dry-run, nothing written)" : "")}");
    Console.WriteLine($"  skipped       : {totalSkipped:N0}  (source_type filled by another writer between SELECT and UPDATE)");
    Console.WriteLine($"  missing       : {totalMissing:N0}  (no LABEL line for that item; rerun the tool to retry)");
    Console.WriteLine($"  invalid       : {totalInvalid:N0}  (model returned a label outside the canonical 5; rerun to retry)");
    Console.WriteLine();
    Console.WriteLine("  per-type distribution of updated rows:");
    foreach (var (type, count) in perTypeCounts.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"    {type,-12} {count,6:N0}");

    return 0;
}

async Task<BatchResult> ScoreBatchAsync(AnthropicMessageCreator client, string classifierModel, string effortStr, BatchFile batch)
{
    // Re-number items 1..N for the API call so the model sees stable sequence ids.
    var seqToTruth = new Dictionary<int, string>();
    var seqToOriginalId = new Dictionary<int, long>();
    var sb = new StringBuilder();
    for (var i = 0; i < batch.Items.Count; i++)
    {
        var seq = i + 1;
        seqToTruth[seq] = batch.Items[i].TruthLabel!;
        seqToOriginalId[seq] = batch.Items[i].Id;
        sb.AppendLine($"=====BEGIN:{seq}=====");
        sb.AppendLine(batch.Items[i].Summary);
        sb.AppendLine($"=====END:{seq}=====");
        sb.AppendLine();
    }

    var systemPrompt = GetClassifierSystemPrompt();

    var (thinking, maxTokens) = EffortConfig.Resolve(effortStr, baseOutputTokens: 8_192);
    var createParams = new MessageCreateParams
    {
        Model = classifierModel,
        MaxTokens = maxTokens,
        System = systemPrompt,
        Messages = [new MessageParam { Role = Role.User, Content = sb.ToString() }],
    };
    if (thinking != null)
        createParams = createParams with { Thinking = thinking };
    var response = await client.CreateAsync(createParams, CancellationToken.None);

    var responseText = response.Content
        .Where(b => b.TryPickText(out _))
        .Select(b => { b.TryPickText(out var t); return t!.Text; })
        .FirstOrDefault() ?? "";

    var labelPattern = new Regex(@"={5}LABEL:(\d+)=([^=\s]+)={5}");
    var predictions = new Dictionary<int, string>();
    foreach (Match m in labelPattern.Matches(responseText))
    {
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
        predictions[seq] = m.Groups[2].Value.Trim();
    }

    int correct = 0, missing = 0, invalid = 0;
    var confusion = new Dictionary<(string Truth, string Pred), int>();
    var mismatches = new List<MismatchEntry>();
    foreach (var (seq, truth) in seqToTruth)
    {
        if (!predictions.TryGetValue(seq, out var pred)) { missing++; continue; }
        if (!canonicalTypes.Contains(pred)) { invalid++; continue; }
        if (pred == truth) correct++;
        else
        {
            var key = (truth, pred);
            confusion.TryGetValue(key, out var n);
            confusion[key] = n + 1;
            var item = batch.Items[seq - 1];
            mismatches.Add(new MismatchEntry(item.Id, truth, pred, item.Summary));
        }
    }

    // Always write a per-batch mismatch dump alongside the run output.
    if (mismatches.Count > 0)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "classify-eval-mismatches");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"mismatches_{batch.BatchId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(mismatches, Shared.JsonOpts));
    }

    return new BatchResult(
        BatchId: batch.BatchId,
        Total: seqToTruth.Count,
        Correct: correct,
        MissingLabels: missing,
        InvalidLabels: invalid,
        Confusion: confusion,
        Accuracy: seqToTruth.Count > 0 ? (double)correct / seqToTruth.Count : 0);
}

static string GetClassifierSystemPrompt() => """
        You are an expert document classifier for a personal knowledge retrieval
        system over a software engineer's corpus. The summaries you receive were
        LLM-generated from source documents that include meeting transcripts (often
        voice-to-text origin), daily notes, and engineering planning artifacts.
        Your label drives downstream search filtering — precision matters more than
        caution.

        CLASSIFICATION FLOW — apply in order. Stop at the first matching step.

        ────────────────────────────────────────────────────────────────────────────
        STEP 1 — IS THIS A MEETING / TRANSCRIPT SUMMARY?
        ────────────────────────────────────────────────────────────────────────────
        The single most reliable signal in this corpus is whether the underlying
        document is a meeting transcript. Transcripts have a distinctive flow even
        after summarization: people do things to each other (discuss, agree, push
        back, raise concerns), decisions get made in past tense, action items get
        named owners, and the structure is multi-topic conversational rather than
        single-topic structural.

        Decide YES if you see ANY combination of these (typically several at once):

        CONVERSATIONAL SIGNALS:
          - Named individuals interacting: "Jimmy mentioned", "Anthony agreed",
            "Sam raised", "Patrick said", "the team discussed"
          - First-person plural: "we discussed", "we agreed", "our team", "we'll"
          - Past-tense decisions: "agreed to", "decided", "concluded", "resolved",
            "settled on", "signed off on"
          - Action items with owners: "Anthony to write the memo", "Caleb will
            review", "owner: <name>", "<name> took the action"
          - Quoted or paraphrased speech: "<name> said", "<name> pushed back on",
            "<name> raised concerns about"
          - Cross-group dynamics: "engineering wanted X but product was concerned"

        STRUCTURAL SIGNALS:
          - Meeting-type words in the opening: "X sync", "X retro", "X standup",
            "X review", "X session", "X meeting", "X 1:1", "X chat", "X discussion",
            "X intake", "X check-in", "X planning session"
          - Multiple distinct topics covered in sequence (meeting-agenda shape)
          - Time / cadence markers: "this morning's sync", "yesterday's standup",
            "weekly retro", "Q1 planning meeting"
          - Enumerated attendees, explicit or implicit: "Attendees:", "Present:",
            "Patrick and Anthony"
          - Open questions, parking-lot items, "TBD", "still need to decide"

        SPEECH-TO-TEXT RESIDUE (the source was voice transcription):
          - Mangled proper nouns or odd capitalization on technical terms
          - In-room acronym usage without expansion
          - Casual phrasing the summarizer kept: "the team got pretty animated",
            "there was a lot of back and forth on", "we landed on"
          - Topic-jumping or rambling structure in the underlying flow

        If YES → go to STEP 2.
        If NO  → go to STEP 4.

        ────────────────────────────────────────────────────────────────────────────
        STEP 2 — IS IT A 1:1?
        ────────────────────────────────────────────────────────────────────────────
        YES if BOTH:
          - Exactly two named participants, typically the operator + one other
          - Single deep conversation thread (career/feedback/cross-functional
            alignment) rather than multi-topic agenda
        Common phrasing: "<name> 1:1", "<name>/<name> sync", "<name> and <name> met"

        → Label: 1on1
        → Otherwise: STEP 3

        ────────────────────────────────────────────────────────────────────────────
        STEP 3 — IS IT A STANDUP?
        ────────────────────────────────────────────────────────────────────────────
        YES if:
          - Per-person status fragments (each person reports what they're on)
          - Brief and structured; the summary is short
          - Identifying words: "daily standup", "DSU", "daily sync", "scrum"
          - Format: yesterday / today / blockers

        → Label: standup
        → Otherwise: transcript  (the meeting catch-all)

        ────────────────────────────────────────────────────────────────────────────
        STEP 4 — PLANNING vs NOTE: ASK ABOUT AUDIENCE AND INTENT
        ────────────────────────────────────────────────────────────────────────────
        You've ruled out a meeting. Now decide between `planning` and `note`. The
        cut here is NOT about length, formality, or whether the summary mentions
        acceptance criteria. The cut is about AUDIENCE and INTENT.

        THE FUNDAMENTAL QUESTION: who is this document written for, and is there
        a next actor?

        PLANNING — written for a next actor (someone who will build, decide on,
        or use this). Defines work, approach, scope, or a system change. The
        operator could hand this to a teammate and the teammate could DO
        SOMETHING with it.

          Surface signals in the summary:
            - Leads with what's being changed / built / added / proposed:
              "the endpoint adds", "this story implements", "the approach is",
              "we need to support", "proposed approach", "this PR / story / PBI"
            - Third-person / passive voice describing the artifact rather than
              a person: "validates the input", "rolls out behind a flag"
            - References work-item / story / ticket numbers, named features,
              named systems, named endpoints, named code paths
            - Focus on a system or feature change, not on what people did or
              thought
            - Even short summaries count. "We need to support X for parts boxes"
              is a story body — somebody is going to build the thing.

          Sources that produce planning: implementation plans, technical specs,
          design docs, story / PBI / ticket / work-item bodies (even brief ones),
          RPIV session files, refinement output, scope docs, README-style intent
          docs, architecture proposals.

        NOTE — written BY the operator FOR the operator, capturing what they
        observed, thought, or did in the moment. First-person. No implied next
        actor. Nothing to be acted on by anyone else.

          Surface signals in the summary:
            - First-person framing: "I noticed", "I'm thinking", "today I"
            - Date-stamped journal feel: "notes from today", "today's entry",
              "thoughts on", "quick thought after the retro"
            - Mix of observation, reaction, and personal follow-up items
            - Mentions casual sources: "Jim mentioned at lunch", "saw in Slack"
            - Reference / glossary / alias content the operator maintains for
              themselves to remember

          Sources that produce note: daily journal entries (YYYY-MM-DD.md),
          personal observations, post-meeting reactions the operator wrote down
          for themselves, lists of things to look into, alias lists, glossaries,
          generic reference markdown.

        KEY TEST: imagine handing the SOURCE document (not the summary) to a
        teammate. If they could read it and DO SOMETHING — build it, decide on
        it, hand it off, use it — that's `planning`. If only the operator gets
        value out of it because it's their personal sense-making — that's `note`.

        ANTI-DEFAULT: do not use `note` as a fallback when uncertain. A summary
        describing what to build, what changed, what was proposed, what's in
        scope, or what someone should do IS `planning`. Reach for `note` ONLY
        when the document is genuinely the operator processing the moment for
        themselves with no next actor.

        BORDERLINE CASES — apply these carve-outs before deciding:

        1. RECORDS OF PAST WORK → depends on what was shipped.
           - Commits / activity logs that shipped CODE or RUNTIME behavior
             (refactor, bug fix, feature implementation, deployment, config
             change to a running system) → NOTE. The work already shipped;
             there is no next actor.
           - Commits / activity logs whose substance IS a planning artifact
             (story file, feature spec, rules doc, design doc, refinement
             output, scope change) → classify by the substance of the
             artifact, almost always PLANNING. The "Commit XYZ by <person>"
             framing does NOT reclassify a planning artifact as note.
           Test: does the commit describe a system change that already
           shipped, or does it describe an artifact someone will act on?
           Past-tense framing alone is not enough to call note.

        2. REFERENCE / GUIDE DOCS → depends on audience.
           - A guide, standard, rules doc, or contributor doc that defines
             conventions for OTHERS to follow (style guide for teammates,
             coding standard for a team, agent rules document, skill
             definition, how-to guide for users) is PLANNING. The next
             actor is the future implementer or writer who will apply the
             rules.
           - A personal cheat-sheet, memory aid, glossary, or alias list the
             operator wrote for themselves is NOTE. Only the operator
             consumes it.

        3. INVESTIGATIONS / ANALYSIS → depends on disposition.
           - Analysis that EXPLICITLY concludes "no changes needed" /
             "no action required" / "behavior is by design" with zero
             follow-up → NOTE.
           - Investigation that articulates a problem to solve, names a
             follow-up, proposes an approach, surfaces recommendations,
             or identifies missing evidence to chase → PLANNING.
           When in doubt, prefer PLANNING. Most investigations exist
           because there's something to act on.

        → Audience is "next actor"        → planning
        → Audience is "operator-in-the-moment" → note

        EXAMPLES:

          Summary: "Engineer sync discussing field operations friction and roadmap
          bottlenecks. Primary issues: a significant portion of engineering work
          doesn't ship due to ops blocking. Resolution: dedicate 10% of velocity
          to autonomous improvements. Jimmy agreed the dynamic was imbalanced."
          → transcript
          (named individuals interacting, decisions in past tense, multi-topic)

          Summary: "Alex and Sam 1:1 on 2026-04-22. Agreed to defer the Phoenix
          migration until Q3. Sam to write the deferral memo and circulate."
          → 1on1
          (two named participants, single deep thread)

          Summary: "Daily standup. Derek wrapping API cleanup. Caleb reviewing
          stories. Matt finished testing on UVW Light."
          → standup
          (per-person status, "daily standup")

          Summary: "Implementation plan for the SaveAssignmentFlags endpoint. Adds
          an optional message field to capture error context per flag. Touches
          three repos; rolls out behind a feature flag."
          → planning
          (artifact written for the implementer; describes a system change with
          a next actor)

          Summary: "Need to handle the case where multi-pricing requests come in
          for parts boxes. Currently no manual override exists. Proposed approach:
          intercept the request, allow operator override, fall back to estimate."
          → planning
          (work-item body — somebody is going to build this; clear next actor
          even though the summary is short)

          Summary: "The DSU refactoring story. Splits the existing daily-status
          collector into a producer / consumer pair so per-team failures don't
          block the global run."
          → planning
          (story body describing a system change to be built)

          Summary: "Glossary of internal product names so the agent recognizes
          alternates: Phoenix = Project Phoenix = Phenix; Atlas = AWS Atlas."
          → note
          (reference content the operator maintains for themselves; no next
          actor)

          Summary: "Notes from today. Jim mentioned the Phoenix thing at lunch;
          need to follow up. Picked up coffee for the team."
          → note
          (date-stamped journal entry; first-person; operator's own record)

          Summary: "Quick thought after the retro: I think we're underestimating
          the migration risk. Need to bring this up next week."
          → note
          (operator's personal reaction; first-person; no next actor in the
          document itself)

          Summary: "On 2026-04-18, Patrick committed the Phoenix migration
          batch runner. Splits the input into chunks of 500 and writes per-
          chunk progress to a status file. Touches three files in the
          ingestion module."
          → note
          (commit narrative — code that already shipped, no next actor)

          Summary: "Commit 0195 (2026-02-13, Patrick) completes planning for
          VTrace Core Migration feature 178322 with a feature overview, five
          story files, and documentation. Eight stories span phases P01-P05
          covering YAML pipeline, EntLib shims, multi-target compile, service
          productionization, toggle updates, testing, cleanup."
          → planning
          (commit framing wraps a planning artifact — the substance is a
          feature overview and story files, which someone will implement)

          Summary: "Coding rules guide for the team: prefer await over
          Task.FromResult; keep tests to a single assertion using
          BeEquivalentTo; never delete tests without explicit approval."
          → planning
          (guide that defines conventions for OTHERS to follow; the next
          actor is the future implementer who applies the rules)

          Summary: "Investigation into the duplicate-job warnings. Cross-
          referenced the queue logs and worker config. Behavior is by design
          — the consumer dedupes downstream. No code changes needed."
          → note
          (analysis that concludes nothing needs to change; describes
          existing behavior, no proposed work)

        STEP 5 (FALLBACK): If you somehow reach this step without picking a label,
        choose `note`. But you should almost never reach this step.

        INPUT FORMAT:
            =====BEGIN:N=====
            (summary text)
            =====END:N=====

        OUTPUT CONTRACT (CRITICAL — exact format, no exceptions):
        For each input block, produce exactly one output line:

            =====LABEL:N=<value>=====

        Where N matches the input block's N and <value> is exactly one of:
        transcript, standup, 1on1, planning, note (lowercase, no quotes). Produce
        labels in ascending N order. Do NOT produce any text outside LABEL lines.
        No reasoning, no markdown, no explanations.
        """;

static List<(long Id, string Summary)> LoadUnclassifiedSummaries(string dbPath)
{
    var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
    using var conn = new SqliteConnection(connStr);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText =
        "SELECT id, summary FROM files WHERE source_type IS NULL AND summary IS NOT NULL AND summary != '' ORDER BY id";
    using var reader = cmd.ExecuteReader();
    var rows = new List<(long, string)>();
    while (reader.Read())
        rows.Add((reader.GetInt64(0), reader.GetString(1)));
    return rows;
}

static (string apiKey, string? vertexBaseUrl) ReadApiCreds(string configPath)
{
    var apiKeyEnv = "ANTHROPIC_API_KEY";
    string? vertexBaseUrl = null;
    if (File.Exists(configPath))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("second_brain", out var sb))
            {
                if (sb.TryGetProperty("anthropic_api_key_env", out var k)) apiKeyEnv = k.GetString() ?? apiKeyEnv;
                if (sb.TryGetProperty("vertex_base_url", out var v))
                {
                    var raw = v.GetString();
                    if (!string.IsNullOrEmpty(raw)) vertexBaseUrl = raw;
                }
            }
        }
        catch { /* fall through with defaults */ }
    }
    return (Environment.GetEnvironmentVariable(apiKeyEnv) ?? "", vertexBaseUrl);
}

static string DefaultDbPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SecondBrainMcpServer", "index", "fts.db");

static string DefaultConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SecondBrainMcpServer", "mcp_config.json");

static string? ArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  classify-eval prep     [--db <path>] [--out <dir>]");
    Console.WriteLine("  classify-eval run      [--in <dir>] [--config <mcp_config.json>] [--model <id>] [--effort low|medium|high]");
    Console.WriteLine("  classify-eval backfill [--db <path>] [--config <mcp_config.json>] [--model <id>] [--effort low|medium|high]");
    Console.WriteLine("                         [--batch-kb <int>] [--max-batches <int>] [--dry-run]");
}

internal static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

internal sealed record BatchItem(long Id, string Summary, string? TruthLabel);

internal sealed record BatchFile(string BatchId, int TargetSizeKb, int ApproximateBytes, List<BatchItem> Items);

internal sealed record LabelEntry(long Id, string Label);

internal sealed record LabelsFile(List<LabelEntry> Labels);

internal sealed record MismatchEntry(long Id, string TruthLabel, string PredictedLabel, string Summary);

internal sealed record BatchResult(
    string BatchId,
    int Total,
    int Correct,
    int MissingLabels,
    int InvalidLabels,
    Dictionary<(string Truth, string Pred), int> Confusion,
    double Accuracy);
