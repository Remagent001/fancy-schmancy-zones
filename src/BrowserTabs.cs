using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace FancySchmancyZones;

/// <summary>
/// Switches a browser window back to the tab a layout was saved on.
///
/// A layout stores each window's TITLE, and a browser window's title is exactly its active tab
/// (plus a " - Google Chrome"-style suffix) — so every layout Keith has ever locked already knows
/// which tab he was on; nothing new needs capturing. What was missing is the way back: when a flip
/// places a browser window that has since wandered to another tab, this walks the window's real
/// tab strip via UI Automation (the same machinery BrowserProfiles already uses), finds the tab
/// whose title matches the saved one, and selects it. If that tab was closed, the window is left
/// exactly as it is — we re-select, we never re-open.
/// </summary>
public static class BrowserTabs
{
    /// <summary>Browsers whose tab strip we know how to read. Chromium family for sure; Firefox
    /// exposes its tabs the same way, so it's allowed to try (worst case: "no tab strip", no-op).</summary>
    public static bool CanRestore(string proc) =>
        WindowManager.IsChromium(proc) ||
        proc.Equals("firefox", StringComparison.OrdinalIgnoreCase);

    // "<page> - Google Chrome" -> "<page>". Handles the Beta/Dev/Canary channels, Edge, Brave,
    // Firefox (which uses an em dash), Opera/Vivaldi, and the private-window tags in both shapes:
    // Chrome's trailing "(Incognito)", Edge's mid-title "- [InPrivate] -", and modern Firefox's
    // paren-less "Private Browsing". A title with no recognized suffix passes through unchanged —
    // the prefix matching in TrySelectTab still gives it a fair shot.
    private static readonly Regex BrowserSuffix = new(
        @"^(?<page>.*?)(\s+[-–—]\s+\[InPrivate\])?\s+[-–—]\s+(Google Chrome|Microsoft Edge|Brave|Mozilla Firefox|Opera|Vivaldi)" +
        @"(\s+(Beta|Dev|Canary))?(\s+\((Incognito|InPrivate|Private Browsing)\)|\s+Private Browsing)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Edge window titles grow " and 3 more pages - <profile>" when several tabs are open; the tab
    // itself is named without that, so strip it or the exact match can never fire on Edge.
    private static readonly Regex EdgeMorePages = new(
        @"\s+and\s+\d+\s+more\s+(?:pages?|tabs?)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The page/tab part of a browser window title (the saved layout keeps whole titles).</summary>
    public static string PageTitle(string windowTitle)
    {
        var m = BrowserSuffix.Match(windowTitle.Trim());
        string page = m.Success ? m.Groups["page"].Value.Trim() : windowTitle.Trim();
        return EdgeMorePages.Replace(page, "").Trim();
    }

    /// <summary>
    /// Find the saved page's tab in this window's tab strip and select it. Returns true if a tab
    /// was matched (and is now, or already was, the active one); detail carries a short human
    /// explanation for flip.log either way. Never throws — a browser that won't talk UIA right now
    /// just reports itself and is left alone.
    /// </summary>
    public static bool TrySelectTab(IntPtr hwnd, string savedTitle, out string detail)
    {
        try
        {
            string page = PageTitle(savedTitle);
            if (page.Length < 2) { detail = "saved title too short to match"; return false; }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) { detail = "window has no automation tree"; return false; }

            var strip = FindTabStrip(root);
            if (strip == null) { detail = "no tab strip exposed"; return false; }

            var items = strip.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            // Exact title first; otherwise the longest prefix agreement, with the two directions
            // held to different bars. A tab name EXTENDING the saved title is the normal drift
            // (Chrome hangs "- Memory usage - 438 MB"-style hints on tab names; verified live), so
            // it just needs the 8-char floor. The reverse — a tab name that's only a PREFIX of the
            // saved title — must cover most of it, or a short look-alike steals the match: a saved
            // "Facebook Watch" tab that's been closed must NOT land on a pinned "Facebook" tab
            // (review finding). Genuine truncation drift still clears the 75% bar.
            AutomationElement? best = null;
            int bestLen = 0;
            foreach (AutomationElement t in items)
            {
                string name;
                try { name = (t.Current.Name ?? "").Trim(); } catch { continue; }
                if (name.Length == 0) continue;
                if (name.Equals(page, StringComparison.OrdinalIgnoreCase)) { best = t; break; }
                int overlap = Math.Min(name.Length, page.Length);
                bool tabExtendsSaved = name.StartsWith(page, StringComparison.OrdinalIgnoreCase);
                bool tabIsMostOfSaved = page.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                                        name.Length >= page.Length * 3 / 4;
                if (overlap >= 8 && overlap > bestLen && (tabExtendsSaved || tabIsMostOfSaved))
                { best = t; bestLen = overlap; }
            }
            if (best == null)
            {
                detail = $"tab \"{Trunc(page)}\" not in this window ({items.Count} tabs) — tab closed, or it lives in another window";
                return false;
            }

            if (best.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object s) &&
                s is SelectionItemPattern sel)
            {
                if (!sel.Current.IsSelected) sel.Select();
                detail = $"switched to \"{Trunc(best.Current.Name ?? page)}\"";
                return true;
            }
            if (best.TryGetCurrentPattern(InvokePattern.Pattern, out object i) && i is InvokePattern inv)
            {
                inv.Invoke();
                detail = $"switched to \"{Trunc(best.Current.Name ?? page)}\" (invoke)";
                return true;
            }
            detail = "tab found but exposes no way to select it";
            return false;
        }
        catch (Exception ex)
        {
            detail = "error: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The browser's REAL tab strip: the first Tab control found WITHOUT ever stepping into the
    /// page. A web page can legitimately contain its own role="tablist" (which also maps to
    /// ControlType.Tab), and "first in tree order" is NOT a safe tie-break — verified live on a
    /// Costco product page whose in-page spec/image tabs were found ahead of the window's own
    /// strip, so a "tab restore" would have clicked around inside Keith's page. Page content
    /// always lives under the Document element, so this walks breadth-first through the browser's
    /// own UI and refuses to descend into any Document — which also keeps the walk small and
    /// bounded (the strip sits a few levels deep; the page is 99% of the tree and all of the
    /// cost), instead of a FindAll over the entire window that stalls on a heavy page.
    /// (Public so the test harness exercises exactly this logic.)
    /// </summary>
    public static AutomationElement? FindTabStrip(AutomationElement root)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement el, int depth)>();
        queue.Enqueue((root, 0));
        int budget = 400;                       // browser chrome is a few hundred nodes at most
        while (queue.Count > 0)
        {
            var (el, depth) = queue.Dequeue();
            if (depth >= 8) continue;           // the strip is shallow; don't crawl the world
            AutomationElement? c;
            try { c = walker.GetFirstChild(el); } catch { continue; }
            while (c != null)
            {
                if (--budget <= 0) return null;
                try
                {
                    var ct = c.Current.ControlType;
                    if (!Equals(ct, ControlType.Document))   // never enter web content
                    {
                        if (Equals(ct, ControlType.Tab)) return c;
                        queue.Enqueue((c, depth + 1));
                    }
                    c = walker.GetNextSibling(c);
                }
                catch { break; }                // tree changed under us — try the next branch
            }
        }
        return null;
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
