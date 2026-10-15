using System;
using System.Collections.Generic;
using System.Globalization;

namespace CookieJar.Platform.Mac
{
    /// <summary>One parsed row of `ps -axo pid=,ppid=,rss=,comm=`.</summary>
    public sealed class MacProcessRow
    {
        public MacProcessRow(int pid, int parentPid, long rssBytes, string name)
        {
            Pid = pid; ParentPid = parentPid; RssBytes = rssBytes; Name = name;
        }
        public int Pid { get; }
        public int ParentPid { get; }
        public long RssBytes { get; }
        /// <summary>Basename of the executable (no directory part).</summary>
        public string Name { get; }
    }

    /// <summary>Pure parsing of ps output and tree walks. No I/O; never throws.</summary>
    public static class MacProcessParsing
    {
        // Same depth guard as ProcessTree.MaxTreeDepth.
        private const int MaxTreeDepth = 64;

        /// <summary>Parses ps output. Malformed lines are skipped.</summary>
        public static List<MacProcessRow> ParsePs(string text)
        {
            var rows = new List<MacProcessRow>();
            if (string.IsNullOrEmpty(text)) return rows;
            foreach (string line in text.Split('\n'))
            {
                MacProcessRow row;
                if (TryParseLine(line, out row)) rows.Add(row);
            }
            return rows;
        }

        /// <summary>Parses one "pid ppid rss comm" line. rss is reported by ps in KB and returned in bytes.</summary>
        public static bool TryParseLine(string line, out MacProcessRow row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string s = line.Trim();
            int p = 0;
            var numbers = new long[3];
            for (int i = 0; i < 3; i++)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
                int start = p;
                while (p < s.Length && !char.IsWhiteSpace(s[p])) p++;
                if (start == p) return false;
                long value;
                if (!long.TryParse(s.Substring(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0)
                {
                    return false;
                }
                numbers[i] = value;
            }

            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            string name = BaseName(s.Substring(p).TrimEnd());
            if (name.Length == 0) return false;
            if (numbers[0] > int.MaxValue || numbers[1] > int.MaxValue) return false;

            row = new MacProcessRow((int)numbers[0], (int)numbers[1], numbers[2] * 1024L, name);
            return true;
        }

        /// <summary>Returns the part after the last '/', so comm paths and bare names both match by name.</summary>
        public static string BaseName(string comm)
        {
            if (string.IsNullOrEmpty(comm)) return "";
            int slash = comm.LastIndexOf('/');
            return slash < 0 ? comm : comm.Substring(slash + 1);
        }

        /// <summary>
        /// Descendants of root, children before their parents (post-order), root NOT included.
        /// Mirrors ProcessTree.PostOrder with includeRoot = false. Cycle-safe.
        /// </summary>
        public static List<int> Descendants(IReadOnlyList<MacProcessRow> rows, int root)
        {
            var output = new List<int>();
            if (rows == null || root <= 0) return output;

            var children = new Dictionary<int, List<int>>();
            foreach (MacProcessRow row in rows)
            {
                if (row.Pid == row.ParentPid) continue;
                List<int> kids;
                if (!children.TryGetValue(row.ParentPid, out kids))
                {
                    kids = new List<int>();
                    children[row.ParentPid] = kids;
                }
                kids.Add(row.Pid);
            }

            var visited = new HashSet<int>();
            visited.Add(root);
            Walk(children, root, 0, visited, output);
            return output;
        }

        /// <summary>Descendants followed by root last. Same as ProcessTree.GetTreeLeavesFirst.</summary>
        public static List<int> TreeLeavesFirst(IReadOnlyList<MacProcessRow> rows, int root)
        {
            List<int> output = Descendants(rows, root);
            if (root > 0) output.Add(root);
            return output;
        }

        private static void Walk(Dictionary<int, List<int>> children, int parent, int depth, HashSet<int> visited, List<int> output)
        {
            if (depth >= MaxTreeDepth) return;

            List<int> kids;
            if (!children.TryGetValue(parent, out kids)) return;

            foreach (int kid in kids)
            {
                if (!visited.Add(kid)) continue;
                Walk(children, kid, depth + 1, visited, output);
                output.Add(kid);
            }
        }
    }
}
