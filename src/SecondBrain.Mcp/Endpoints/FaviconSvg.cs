namespace SecondBrain.Mcp.Endpoints;

internal static class FaviconSvg
{
    internal const string Content = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32">
          <defs>
            <linearGradient id="stroke-grad" x1="0" y1="0" x2="32" y2="32" gradientUnits="userSpaceOnUse">
              <stop offset="0%" stop-color="#4F46E5"/>
              <stop offset="100%" stop-color="#06B6D4"/>
            </linearGradient>
            <linearGradient id="node-grad" x1="0" y1="0" x2="32" y2="32" gradientUnits="userSpaceOnUse">
              <stop offset="0%" stop-color="#818CF8"/>
              <stop offset="100%" stop-color="#22D3EE"/>
            </linearGradient>
          </defs>
          <path d="M 6 25 C 3 25, 2 21, 2 18 C 2 13, 4 9, 7 7 C 10 5, 13 4, 16 4 C 19 4, 22 5, 25 7 C 28 9, 30 13, 30 18 C 30 21, 29 25, 26 25 Z"
            fill="#4F46E5" fill-opacity="0.12"
            stroke="url(#stroke-grad)" stroke-width="1.75" stroke-linejoin="round"/>
          <line x1="9"  y1="15" x2="16" y2="13" stroke="url(#stroke-grad)" stroke-width="1.1" stroke-opacity="0.7"/>
          <line x1="16" y1="13" x2="23" y2="15" stroke="url(#stroke-grad)" stroke-width="1.1" stroke-opacity="0.7"/>
          <line x1="9"  y1="15" x2="10" y2="21" stroke="url(#stroke-grad)" stroke-width="1.1" stroke-opacity="0.7"/>
          <line x1="23" y1="15" x2="22" y2="21" stroke="url(#stroke-grad)" stroke-width="1.1" stroke-opacity="0.7"/>
          <line x1="10" y1="21" x2="22" y2="21" stroke="url(#stroke-grad)" stroke-width="1.1" stroke-opacity="0.7"/>
          <circle cx="9"  cy="15" r="2"   fill="url(#node-grad)" stroke="#ffffff" stroke-width="0.75"/>
          <circle cx="10" cy="21" r="1.7" fill="url(#node-grad)" stroke="#ffffff" stroke-width="0.6"/>
          <circle cx="16" cy="13" r="2.5" fill="url(#node-grad)" stroke="#ffffff" stroke-width="0.75"/>
          <circle cx="23" cy="15" r="2"   fill="url(#node-grad)" stroke="#ffffff" stroke-width="0.75"/>
          <circle cx="22" cy="21" r="1.7" fill="url(#node-grad)" stroke="#ffffff" stroke-width="0.6"/>
        </svg>
        """;
}
