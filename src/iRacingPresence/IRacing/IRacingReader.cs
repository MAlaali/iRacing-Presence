using System.IO.MemoryMappedFiles;
using System.Text;

namespace IRacingPresence.IRacing;

readonly record struct VarHeader(int Type, int Offset, int Count);

/// One consistent copy of the latest telemetry buffer plus the parsed session-info YAML.
sealed class TelemetrySnapshot(byte[] buffer, IReadOnlyDictionary<string, VarHeader> vars, Dictionary<string, object> sessionInfo)
{
    public Dictionary<string, object> SessionInfo { get; } = sessionInfo;

    public double? Number(string name)
    {
        if (!vars.TryGetValue(name, out var h) || h.Offset < 0)
            return null;
        int size = h.Type switch { 0 or 1 => 1, 2 or 3 or 4 => 4, 5 => 8, _ => 0 };
        if (size == 0 || h.Offset + size > buffer.Length)
            return null;
        return h.Type switch
        {
            0 or 1 => buffer[h.Offset],
            2 or 3 => BitConverter.ToInt32(buffer, h.Offset),
            4 => BitConverter.ToSingle(buffer, h.Offset),
            _ => BitConverter.ToDouble(buffer, h.Offset),
        };
    }

    public int Int(string name, int fallback = 0) => Number(name) is double d ? (int)d : fallback;
    public double Double(string name, double fallback = double.NaN) => Number(name) ?? fallback;
    public bool Bool(string name) => Number(name) is double d && d != 0;
}

/// Reads the iRacing SDK shared memory ("Local\IRSDKMemMapFileName"), the same source every
/// overlay and telemetry tool uses. Layout follows irsdk_defines.h from the official SDK.
sealed class IRacingReader(string memMapName = IRacingReader.MemMapName) : IDisposable
{
    public const string MemMapName = @"Local\IRSDKMemMapFileName";

    // irsdk_header field offsets
    const int StatusOffset = 4, SessionInfoUpdateOffset = 12, SessionInfoLenOffset = 16, SessionInfoOffsetOffset = 20;
    const int NumVarsOffset = 24, VarHeaderOffsetOffset = 28, NumBufOffset = 32, BufLenOffset = 36, VarBufOffset = 48;
    const int VarBufSize = 16, VarHeaderSize = 144, VarNameOffset = 16, MaxString = 32, MaxBufs = 4;
    const int StatusConnected = 1;

    static readonly Encoding YamlEncoding = CreateYamlEncoding();
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    int _lastTick = int.MinValue;
    DateTime _lastTickChange = DateTime.UtcNow;
    MemoryMappedFile? _file;
    MemoryMappedViewAccessor? _view;
    Dictionary<string, VarHeader> _vars = new(StringComparer.Ordinal);
    int _numVars = -1, _varHeaderOffset = -1;
    int _sessionInfoUpdate = int.MinValue;
    Dictionary<string, object> _sessionInfo = new();

    /// Returns null when the sim isn't running (or is still loading).
    public TelemetrySnapshot? Read()
    {
        if (_view == null && !Open())
            return null;
        var view = _view!;

        if ((view.ReadInt32(StatusOffset) & StatusConnected) == 0)
        {
            Close();
            return null;
        }

        int numVars = view.ReadInt32(NumVarsOffset);
        int varHeaderOffset = view.ReadInt32(VarHeaderOffsetOffset);
        if (numVars != _numVars || varHeaderOffset != _varHeaderOffset)
            LoadVarHeaders(view, numVars, varHeaderOffset);

        int update = view.ReadInt32(SessionInfoUpdateOffset);
        if (update != _sessionInfoUpdate)
            LoadSessionInfo(view);

        var buffer = ReadLatestBuffer(view, out int tick);
        if (buffer == null)
            return null;

        // If the sim crashes it never clears the status flag, so treat frozen data as gone.
        var now = DateTime.UtcNow;
        if (tick != _lastTick)
        {
            _lastTick = tick;
            _lastTickChange = now;
        }
        else if (now - _lastTickChange > StaleAfter)
        {
            Close();
            return null;
        }
        return new TelemetrySnapshot(buffer, _vars, _sessionInfo);
    }

    bool Open()
    {
        try
        {
            _file = MemoryMappedFile.OpenExisting(memMapName, MemoryMappedFileRights.Read);
            _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            Close();
            return false;
        }
    }

    void LoadVarHeaders(MemoryMappedViewAccessor view, int numVars, int varHeaderOffset)
    {
        var vars = new Dictionary<string, VarHeader>(StringComparer.Ordinal);
        var nameBytes = new byte[MaxString];
        for (int i = 0; i < numVars; i++)
        {
            long at = varHeaderOffset + (long)i * VarHeaderSize;
            if (at + VarHeaderSize > view.Capacity)
                break;
            view.ReadArray(at + VarNameOffset, nameBytes, 0, MaxString);
            int len = Array.IndexOf(nameBytes, (byte)0);
            var name = Encoding.ASCII.GetString(nameBytes, 0, len < 0 ? MaxString : len);
            vars[name] = new VarHeader(view.ReadInt32(at), view.ReadInt32(at + 4), view.ReadInt32(at + 8));
        }
        _vars = vars;
        _numVars = numVars;
        _varHeaderOffset = varHeaderOffset;
    }

    void LoadSessionInfo(MemoryMappedViewAccessor view)
    {
        // The sim can rewrite the YAML while we copy it; the update counter tells us if it did.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int update = view.ReadInt32(SessionInfoUpdateOffset);
            int len = view.ReadInt32(SessionInfoLenOffset);
            int offset = view.ReadInt32(SessionInfoOffsetOffset);
            if (len <= 0 || offset <= 0 || offset + (long)len > view.Capacity)
                return;

            var bytes = new byte[len];
            view.ReadArray(offset, bytes, 0, len);
            if (view.ReadInt32(SessionInfoUpdateOffset) != update)
                continue;

            int end = Array.IndexOf(bytes, (byte)0);
            var yaml = YamlEncoding.GetString(bytes, 0, end < 0 ? len : end);
            _sessionInfo = SessionYaml.Parse(yaml);
            _sessionInfoUpdate = update;
            return;
        }
    }

    byte[]? ReadLatestBuffer(MemoryMappedViewAccessor view, out int tick)
    {
        tick = 0;
        int numBuf = Math.Clamp(view.ReadInt32(NumBufOffset), 1, MaxBufs);
        int bufLen = view.ReadInt32(BufLenOffset);
        if (bufLen <= 0)
            return null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int latest = 0, latestTick = int.MinValue;
            for (int i = 0; i < numBuf; i++)
            {
                int bufTick = view.ReadInt32(VarBufOffset + i * VarBufSize);
                if (bufTick > latestTick)
                {
                    latestTick = bufTick;
                    latest = i;
                }
            }

            long tickAt = VarBufOffset + latest * VarBufSize;
            int bufOffset = view.ReadInt32(tickAt + 4);
            if (bufOffset <= 0 || bufOffset + (long)bufLen > view.Capacity)
                return null;

            var buffer = new byte[bufLen];
            view.ReadArray(bufOffset, buffer, 0, bufLen);
            if (view.ReadInt32(tickAt) == latestTick)
            {
                tick = latestTick;
                return buffer;
            }
        }
        return null;
    }

    void Close()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
        _numVars = _varHeaderOffset = -1;
        _sessionInfoUpdate = int.MinValue;
    }

    public void Dispose() => Close();

    static Encoding CreateYamlEncoding()
    {
        // iRacing writes the session string in Windows-1252.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
