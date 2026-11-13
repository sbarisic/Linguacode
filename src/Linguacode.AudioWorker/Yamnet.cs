using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Linguacode.AudioWorker;

/// <summary>Executes the original SavedModel serving signature through TensorFlow's C API.</summary>
internal sealed class Yamnet : IDisposable
{
    private const string Library = "tensorflow.dll";
    private readonly Handle graph;
    private readonly Handle session;
    private readonly Port input;
    private readonly Port scores;
    private readonly JsonArray classes = new();
    private readonly string[] labels;
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly Action<nint> release;
        internal Handle(nint value, Action<nint> release) : base(true) { SetHandle(value); this.release = release; }
        public nint Value { get { if (IsInvalid || IsClosed) throw new IOException("TensorFlow allocation failed."); return DangerousGetHandle(); } }
        protected override bool ReleaseHandle() { release(handle); return true; }
    }
    private static Handle Own(nint value, Action<nint> release) => new(value, release);
    [StructLayout(LayoutKind.Sequential)] private struct Port { public nint Operation; public int Index; }
    [StructLayout(LayoutKind.Sequential)] private struct Buffer { public nint Data; public nuint Length; public nint Deallocator; }
    public Yamnet(string root)
    {
        graph = Own(TF_NewGraph(), TF_DeleteGraph);
        try
        {
            using var status = Own(TF_NewStatus(), TF_DeleteStatus);
            using var options = Own(TF_NewSessionOptions(), TF_DeleteSessionOptions);
            // ConfigProto: intra_op_parallelism_threads=4, inter_op_parallelism_threads=2.
            byte[] config = [0x10, 4, 0x28, 2]; TF_SetConfig(options.Value, config, (nuint)config.Length, status.Value); Check(status);
            using var metadata = Own(TF_NewBuffer(), TF_DeleteBuffer);
            var loaded = TF_LoadSessionFromSavedModel(options.Value, 0, root, ["serve"], 1, graph.Value, metadata.Value, status.Value);
            session = Own(loaded, p => { using var s = Own(TF_NewStatus(), TF_DeleteStatus); TF_CloseSession(p, s.Value); TF_DeleteSession(p, s.Value); });
            Check(status);
            var buffer = Marshal.PtrToStructure<Buffer>(metadata.Value);
            if (buffer.Length > 32 * 1024 * 1024) throw new IOException("Oversized TensorFlow metadata.");
            var bytes = new byte[(int)buffer.Length]; Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            var signature = Proto.Fields(bytes, 5).Select(Proto.Map).FirstOrDefault(m => m.Key == "serving_default").Value
                ?? throw new IOException("YAMNet has no serving signature.");
            var inputs = Proto.Fields(signature, 1).Select(Proto.Map).ToArray();
            var outputs = Proto.Fields(signature, 2).Select(Proto.Map).ToArray();
            if (inputs.Length != 1) throw new IOException("Invalid YAMNet input signature.");
            input = Resolve(Encoding.UTF8.GetString(Proto.Fields(inputs[0].Value, 1).Single()));
            // The original Hub SavedModel names its tuple outputs output_0 (scores), output_1, output_2.
            var scoreInfo = outputs.Single(m => m.Key == "output_0").Value;
            scores = Resolve(Encoding.UTF8.GetString(Proto.Fields(scoreInfo, 1).Single()));
            labels = File.ReadLines(Path.Combine(root, "assets", "yamnet_class_map.csv")).Skip(1).Select(line =>
            {
                var cells = Csv(line); if (cells.Length != 3) throw new IOException("Invalid YAMNet class map.");
                var index = int.Parse(cells[0], System.Globalization.CultureInfo.InvariantCulture);
                if (index != classes.Count) throw new IOException("Unordered YAMNet class map.");
                classes.Add(new JsonObject { ["index"] = cells[0], ["mid"] = cells[1], ["display_name"] = cells[2] }); return cells[2];
            }).ToArray();
            if (labels.Length != 521) throw new IOException("YAMNet requires 521 classes.");
        }
        catch { session?.Dispose(); graph.Dispose(); throw; }
    }
    private Port Resolve(string name)
    {
        var split = name.LastIndexOf(':'); if (split < 0) throw new IOException("Invalid tensor name.");
        var operation = TF_GraphOperationByName(graph.Value, name[..split]);
        if (operation == 0) throw new IOException("YAMNet tensor not found.");
        return new Port { Operation = operation, Index = int.Parse(name[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture) };
    }
    public JsonObject Classify(float[] samples, double offset)
    {
        using var status = Own(TF_NewStatus(), TF_DeleteStatus);
        using var tensor = Own(TF_AllocateTensor(1, [(long)samples.Length], 1, (nuint)(samples.Length * 4)), TF_DeleteTensor);
        Marshal.Copy(samples, 0, TF_TensorData(tensor.Value), samples.Length);
        nint[] outputValues = [0];
        try
        {
            TF_SessionRun(session.Value, 0, [input], [tensor.Value], 1, [scores], outputValues, 1, 0, 0, 0, status.Value); Check(status);
            var output = outputValues[0];
            if (output == 0 || TF_TensorType(output) != 1 || TF_NumDims(output) != 2 || TF_Dim(output, 1) != 521)
                throw new IOException("Invalid YAMNet score tensor.");
            var windows = checked((int)TF_Dim(output, 0));
            if (windows is < 1 or > 251 || TF_TensorByteSize(output) != (nuint)(windows * 521 * 4)) throw new IOException("Invalid YAMNet output size.");
            var values = new float[windows * 521]; Marshal.Copy(TF_TensorData(output), values, 0, values.Length);
            if (values.Any(x => !float.IsFinite(x) || x < 0 || x > 1)) throw new IOException("Invalid YAMNet score.");
            var mean = Enumerable.Range(0, 521).Select(c => Enumerable.Range(0, windows).Average(w => (double)values[w * 521 + c])).ToArray();
            JsonArray Labels(double[] row, int count) => new(Enumerable.Range(0, 521).OrderByDescending(i => row[i]).Take(count)
                .Select(i => (JsonNode)new JsonObject { ["index"] = i, ["label"] = labels[i], ["score"] = row[i] }).ToArray());
            var duration = samples.Length / (double)AudioDecoder.Rate; var rows = new JsonArray();
            for (var w = 0; w < windows && w * .48 < duration; w++)
                rows.Add(new JsonObject { ["startSeconds"] = offset + w * .48, ["endSeconds"] = offset + Math.Min(w * .48 + .96, duration),
                    ["labels"] = Labels(values.AsSpan(w * 521, 521).ToArray().Select(v => (double)v).ToArray(), 5), ["padded"] = w * .48 + .96 > duration });
            return new JsonObject { ["status"] = "succeeded", ["model"] = "Google YAMNet 1", ["engineVersion"] = Marshal.PtrToStringUTF8(TF_Version()),
                ["classMap"] = classes.DeepClone(), ["aggregateScores"] = Labels(mean, 521), ["windows"] = rows, ["windowSeconds"] = .96, ["hopSeconds"] = .48 };
        }
        finally { if (outputValues[0] != 0) TF_DeleteTensor(outputValues[0]); }
    }
    private static string[] Csv(string line)
    {
        var cells = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == ',' && !quoted) { cells.Add(value.ToString()); value.Clear(); } else value.Append(line[i]);
        }
        if (quoted) throw new IOException("Invalid CSV quoting."); cells.Add(value.ToString()); return cells.ToArray();
    }
    private static void Check(Handle status) { if (TF_GetCode(status.Value) != 0) throw new IOException(Marshal.PtrToStringUTF8(TF_Message(status.Value))); }
    public void Dispose() { session.Dispose(); graph.Dispose(); }
    private static class Proto
    {
        // Read only length-delimited protobuf fields; skip all other legal wire types, with bounds checks.
        public static List<byte[]> Fields(byte[] data, int wanted)
        {
            var result = new List<byte[]>(); var position = 0;
            ulong Varint() { ulong n = 0; for (var shift = 0; shift < 64; shift += 7) { if (position >= data.Length) throw new IOException("Truncated protobuf."); var b = data[position++]; n |= (ulong)(b & 127) << shift; if (b < 128) return n; } throw new IOException("Invalid protobuf varint."); }
            while (position < data.Length)
            {
                var tag = Varint(); var wire = tag & 7; var field = tag >> 3;
                if (wire == 0) { Varint(); continue; }
                var length = wire switch { 1 => 8, 5 => 4, 2 => checked((int)Varint()), _ => throw new IOException("Invalid protobuf wire type.") };
                if (length < 0 || length > data.Length - position) throw new IOException("Truncated protobuf field.");
                if (wire == 2 && field == (ulong)wanted) result.Add(data.AsSpan(position, length).ToArray()); position += length;
            }
            return result;
        }
        public static KeyValuePair<string, byte[]> Map(byte[] data) => new(Encoding.UTF8.GetString(Fields(data, 1).Single()), Fields(data, 2).Single());
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_Version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_NewGraph();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteGraph(nint graph);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_NewStatus();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteStatus(nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int TF_GetCode(nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_Message(nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_NewSessionOptions();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteSessionOptions(nint options);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_SetConfig(nint options, byte[] config, nuint length, nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_NewBuffer();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteBuffer(nint buffer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_LoadSessionFromSavedModel(nint options, nint runOptions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] tags, int tagCount, nint graph, nint metaGraph, nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_CloseSession(nint session, nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteSession(nint session, nint status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_GraphOperationByName(nint graph, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_AllocateTensor(int type, long[] dimensions, int rank, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_DeleteTensor(nint tensor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint TF_TensorData(nint tensor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nuint TF_TensorByteSize(nint tensor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int TF_TensorType(nint tensor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int TF_NumDims(nint tensor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern long TF_Dim(nint tensor, int dimension);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void TF_SessionRun(nint session, nint options,
        Port[] inputs, nint[] inputValues, int inputCount, Port[] outputs, [Out] nint[] outputValues, int outputCount, nint targets, int targetCount, nint metadata, nint status);
}
