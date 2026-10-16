using System.Globalization;

namespace IRacingPresence.IRacing;

/// Parser for the YAML subset iRacing writes into shared memory: maps nested by indentation and
/// "- " lists of maps. Values stay raw strings. iRacing doesn't escape driver and team names
/// (colons, '#', quotes), which trips up strict YAML parsers, so this one never fails on them.
static class SessionYaml
{
    sealed record Frame(int Indent, Dictionary<string, object> Map, List<object>? OwnerList, int DashIndent);

    public static Dictionary<string, object> Parse(string yaml)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var stack = new List<Frame> { new(0, root, null, -1) };

        // A key with an empty value may turn out to own a nested map or list on the next line.
        Dictionary<string, object>? pendingMap = null;
        string pendingKey = "";
        int pendingIndent = -1;

        void AddKey(Dictionary<string, object> map, string key, string value, int indent)
        {
            map[key] = value;
            if (value.Length == 0)
            {
                pendingMap = map;
                pendingKey = key;
                pendingIndent = indent;
            }
        }

        void PopDeeperThan(int indent)
        {
            while (stack.Count > 1 && stack[^1].Indent > indent)
                stack.RemoveAt(stack.Count - 1);
        }

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line == "---" || line == "...")
                continue;

            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
                indent++;
            var text = line[indent..];

            if (text == "-" || text.StartsWith("- ", StringComparison.Ordinal))
            {
                int keyIndent = indent + 2;
                var content = text.Length > 2 ? text[2..].Trim() : "";

                PopDeeperThan(keyIndent);
                List<object>? list = null;
                var top = stack[^1];
                if (top.OwnerList != null && top.DashIndent == indent)
                {
                    list = top.OwnerList;
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (pendingMap != null && indent >= pendingIndent)
                {
                    list = new List<object>();
                    pendingMap[pendingKey] = list;
                }
                pendingMap = null;
                if (list == null)
                    continue;

                var item = new Dictionary<string, object>(StringComparer.Ordinal);
                stack.Add(new Frame(keyIndent, item, list, indent));
                if (content.Length == 0)
                {
                    list.Add(item);
                }
                else if (TrySplit(content, out var itemKey, out var itemValue))
                {
                    list.Add(item);
                    AddKey(item, itemKey, itemValue, keyIndent);
                }
                else
                {
                    list.Add(Unquote(content));
                }
                continue;
            }

            if (pendingMap != null && indent > pendingIndent)
            {
                var child = new Dictionary<string, object>(StringComparer.Ordinal);
                pendingMap[pendingKey] = child;
                stack.Add(new Frame(indent, child, null, -1));
            }
            pendingMap = null;
            PopDeeperThan(indent);

            if (TrySplit(text, out var key, out var value))
                AddKey(stack[^1].Map, key, value, indent);
        }

        return root;
    }

    static bool TrySplit(string text, out string key, out string value)
    {
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0)
        {
            key = text[..colon];
            value = Unquote(text[(colon + 2)..].Trim());
            return true;
        }
        if (text.Length > 1 && text[^1] == ':')
        {
            key = text[..^1];
            value = "";
            return true;
        }
        key = value = "";
        return false;
    }

    static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
}

static class YamlNodeExtensions
{
    public static string Str(this Dictionary<string, object>? map, string key) =>
        map != null && map.TryGetValue(key, out var v) && v is string s ? s : "";

    public static int Int(this Dictionary<string, object>? map, string key, int fallback = 0) =>
        int.TryParse(map.Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public static Dictionary<string, object>? Map(this Dictionary<string, object>? map, string key) =>
        map != null && map.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    public static IEnumerable<Dictionary<string, object>> Items(this Dictionary<string, object>? map, string key) =>
        map != null && map.TryGetValue(key, out var v) && v is List<object> list
            ? list.OfType<Dictionary<string, object>>()
            : [];
}
