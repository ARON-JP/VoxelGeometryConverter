using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

record Settings(string InputGeometry, string InputTexture, string OutputGeometry, string OutputTexture,
    string? SourceGeometryId, string OutputGeometryId, string TextureShortName, int Resolution, int AtlasSize, int TileSize);
record PixelImage(int Width, int Height, byte[] Pixels);
record Voxel(int X, int Y, int Z, double Distance, double U, double V);

static class Program
{
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) { Help(); return 0; }
            var settingsPath = Option(args, "--settings");
            if (settingsPath is null) throw new ArgumentException("--settings <file> が必要です。--help で使い方を確認してください。");
            var fullSettingsPath = Path.GetFullPath(settingsPath);
            var basePath = Path.GetDirectoryName(fullSettingsPath)!;
            var raw = JsonNode.Parse(File.ReadAllText(fullSettingsPath))?.AsObject() ?? throw new InvalidDataException("設定 JSON を読めません。");
            string Required(string name) => raw[name]?.GetValue<string>() ?? throw new InvalidDataException($"設定 '{name}' が必要です。");
            var quality = raw["quality"]?.GetValue<string>()?.ToLowerInvariant() ?? "high";
            var resolution = raw["resolution"]?.GetValue<int?>() ?? quality switch { "low" => 48, "medium" => 64, "high" => 96, "ultra" => 128, _ => throw new InvalidDataException("quality は low, medium, high, ultra のいずれかです。") };
            var settings = new Settings(PathFrom(basePath, Required("inputGeometry")), PathFrom(basePath, Required("inputTexture")),
                PathFrom(basePath, Required("outputGeometry")), PathFrom(basePath, Required("outputTexture")), raw["sourceGeometryId"]?.GetValue<string>(),
                Required("outputGeometryId"), Required("textureShortName"), resolution,
                raw["atlasSize"]?.GetValue<int?>() ?? 1024, raw["tileSize"]?.GetValue<int?>() ?? 4);
            Convert(settings);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"失敗: {error.Message}"); return 1; }
    }

    static string PathFrom(string basePath, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
    static string? Option(string[] args, string key) { var index = System.Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    static void Help() => Console.WriteLine("VoxelGeometryConverter --settings <settings.json>\n\nBedrock poly_mesh をキューブ形式へ変換します。settings.example.json を複製して使用してください。");

    static void Convert(Settings s)
    {
        if (s.Resolution < 8 || s.Resolution > 256) throw new InvalidDataException("resolution は 8 ～ 256 にしてください。");
        if (s.AtlasSize <= 0 || s.TileSize <= 0 || s.AtlasSize % s.TileSize != 0) throw new InvalidDataException("atlasSize は tileSize の倍数である必要があります。");
        var root = JsonNode.Parse(File.ReadAllText(s.InputGeometry))?.AsObject() ?? throw new InvalidDataException("入力 geometry JSON を読めません。");
        var geometries = root["minecraft:geometry"]?.AsArray() ?? throw new InvalidDataException("minecraft:geometry がありません。");
        var geometry = geometries.Select(x => x!.AsObject()).FirstOrDefault(x => s.SourceGeometryId is null || x["description"]?["identifier"]?.GetValue<string>() == s.SourceGeometryId)
            ?? throw new InvalidDataException("指定された sourceGeometryId が見つかりません。");
        var mesh = geometry["bones"]?.AsArray().Select(x => x!.AsObject()["poly_mesh"]?.AsObject()).FirstOrDefault(x => x is not null)
            ?? throw new InvalidDataException("入力 geometry に poly_mesh がありません。");
        var positions = mesh["positions"]!.AsArray().Select(V3).ToArray();
        var uvs = mesh["uvs"]!.AsArray().Select(V2).ToArray();
        var polygons = mesh["polys"]!.AsArray().Select(x => x!.AsArray().Select(v => v!.AsArray().Select(n => n!.GetValue<int>()).ToArray()).ToArray()).ToArray();
        var texture = ReadPng(s.InputTexture);
        var voxels = Voxelize(positions, uvs, polygons, s.Resolution);
        var atlasTiles = s.AtlasSize / s.TileSize;
        if (voxels.Count > atlasTiles * atlasTiles) throw new InvalidDataException($"{voxels.Count:N0} キューブに対して atlasSize が小さすぎます。atlasSize を増やすか quality を下げてください。");
        var atlas = new byte[s.AtlasSize * s.AtlasSize * 4];
        var cubes = new List<JsonObject>(voxels.Count);
        var cell = 16.0 / s.Resolution;
        for (var index = 0; index < voxels.Count; index++)
        {
            var voxel = voxels[index]; var px = Math.Clamp((int)Math.Round(voxel.U * (texture.Width - 1)), 0, texture.Width - 1); var py = Math.Clamp((int)Math.Round((1 - voxel.V) * (texture.Height - 1)), 0, texture.Height - 1);
            var source = (py * texture.Width + px) * 4; var tx = index % atlasTiles * s.TileSize; var ty = index / atlasTiles * s.TileSize;
            for (var y = 0; y < s.TileSize; y++) for (var x = 0; x < s.TileSize; x++) { var target = ((ty + y) * s.AtlasSize + tx + x) * 4; atlas[target] = texture.Pixels[source]; atlas[target + 1] = texture.Pixels[source + 1]; atlas[target + 2] = texture.Pixels[source + 2]; atlas[target + 3] = 255; }
            cubes.Add(new JsonObject { ["origin"] = Array(-8 + voxel.X * cell, -8 + voxel.Y * cell, -8 + voxel.Z * cell), ["size"] = Array(cell, cell, cell), ["uv"] = Array(tx, ty), ["uv_size"] = Array(s.TileSize, s.TileSize) });
        }
        var bones = new JsonArray();
        for (var start = 0; start < cubes.Count; start += 1024) { var batch = new JsonArray(); foreach (var cube in cubes.Skip(start).Take(1024)) batch.Add(cube); bones.Add(new JsonObject { ["name"] = $"voxel_part_{start / 1024:D3}", ["pivot"] = Array(0, 0, 0), ["cubes"] = batch }); }
        var output = new JsonObject { ["format_version"] = "1.12.0", ["minecraft:geometry"] = new JsonArray(new JsonObject { ["description"] = new JsonObject { ["identifier"] = s.OutputGeometryId, ["texture_width"] = s.AtlasSize, ["texture_height"] = s.AtlasSize, ["visible_bounds_width"] = 1, ["visible_bounds_height"] = 1, ["visible_bounds_offset"] = Array(0, 0, 0) }, ["bones"] = bones }) };
        Directory.CreateDirectory(Path.GetDirectoryName(s.OutputGeometry)!); Directory.CreateDirectory(Path.GetDirectoryName(s.OutputTexture)!);
        File.WriteAllText(s.OutputGeometry, output.ToJsonString(PrettyJson)); WritePng(s.OutputTexture, s.AtlasSize, s.AtlasSize, atlas);
        Console.WriteLine($"完了: {voxels.Count:N0} cubes / {bones.Count} bones / resolution {s.Resolution}");
        Console.WriteLine($"geometry: {s.OutputGeometryId}\ntexture short name: {s.TextureShortName}");
    }

    static List<Voxel> Voxelize(double[][] positions, double[][] uvs, int[][][] polygons, int resolution)
    {
        var cell = 16.0 / resolution; var threshold2 = .80 * cell * cell; var voxels = new Dictionary<int, Voxel>();
        foreach (var polygon in polygons) for (var n = 1; n < polygon.Length - 1; n++)
        {
            var vertices = new[] { polygon[0], polygon[n], polygon[n + 1] }; var p = vertices.Select(v => positions[v[0]]).ToArray(); var u = vertices.Average(v => uvs[v[2]][0]); var v = vertices.Average(v => uvs[v[2]][1]);
            var start = new int[3]; var end = new int[3]; for (var axis = 0; axis < 3; axis++) { start[axis] = Clamp((int)Math.Floor((p.Min(x => x[axis]) + 8) / cell) - 1, resolution); end[axis] = Clamp((int)Math.Floor((p.Max(x => x[axis]) + 8) / cell) + 1, resolution); }
            for (var x = start[0]; x <= end[0]; x++) for (var y = start[1]; y <= end[1]; y++) for (var z = start[2]; z <= end[2]; z++) { var d = PointTriangleDistanceSquared(new[] { -8 + (x + .5) * cell, -8 + (y + .5) * cell, -8 + (z + .5) * cell }, p[0], p[1], p[2]); if (d <= threshold2) { var key = x + resolution * (y + resolution * z); if (!voxels.TryGetValue(key, out var prior) || d < prior.Distance) voxels[key] = new Voxel(x, y, z, d, u, v); } }
        }
        return voxels.Values.ToList();
    }
    static int Clamp(int value, int resolution) => Math.Max(0, Math.Min(resolution - 1, value));
    static double[] V2(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<double>()).ToArray(); static double[] V3(JsonNode? n) => V2(n);
    static JsonArray Array(params double[] values) => new(values.Select(x => JsonValue.Create(x)).ToArray()); static JsonArray Array(params int[] values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    static double PointTriangleDistanceSquared(double[] p, double[] a, double[] b, double[] c)
    {
        var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(p, a); var d1 = Dot(ab, ap); var d2 = Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return Dot(ap, ap); var bp = Sub(p, b); var d3 = Dot(ab, bp); var d4 = Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return Dot(bp, bp); var vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return Dist(p, Add(a, ab, d1 / (d1 - d3))); var cp = Sub(p, c); var d5 = Dot(ab, cp); var d6 = Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return Dot(cp, cp); var vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return Dist(p, Add(a, ac, d2 / (d2 - d6))); var va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Dist(p, Add(b, Sub(c, b), (d4 - d3) / (d4 - d3 + d5 - d6))); var inv = 1 / (va + vb + vc); return Dist(p, new[] { a[0] + ab[0] * vb * inv + ac[0] * vc * inv, a[1] + ab[1] * vb * inv + ac[1] * vc * inv, a[2] + ab[2] * vb * inv + ac[2] * vc * inv });
    }
    static double[] Add(double[] a, double[] b, double factor) => new[] { a[0] + b[0] * factor, a[1] + b[1] * factor, a[2] + b[2] * factor }; static double Dist(double[] a, double[] b) { var d = Sub(a, b); return Dot(d, d); }
    static PixelImage ReadPng(string path)
    {
        var bytes = File.ReadAllBytes(path); if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("PNG ではありません。"); int offset = 8, width = 0, height = 0, depth = 0, type = 0; using var idat = new MemoryStream();
        while (offset < bytes.Length) { var length = ReadU32(bytes, offset); offset += 4; var kind = Encoding.ASCII.GetString(bytes, offset, 4); offset += 4; if (kind == "IHDR") { width = ReadU32(bytes, offset); height = ReadU32(bytes, offset + 4); depth = bytes[offset + 8]; type = bytes[offset + 9]; } if (kind == "IDAT") idat.Write(bytes, offset, length); offset += length + 4; if (kind == "IEND") break; }
        if (depth != 8 || type != 6) throw new InvalidDataException("8-bit RGBA PNG のみ対応しています。"); idat.Position = 0; using var z = new ZLibStream(idat, CompressionMode.Decompress); using var raw = new MemoryStream(); z.CopyTo(raw); var source = raw.ToArray(); var stride = width * 4; var pixels = new byte[stride * height]; var input = 0;
        for (var y = 0; y < height; y++) { var filter = source[input++]; var row = y * stride; var prior = (y - 1) * stride; for (var x = 0; x < stride; x++) { var value = source[input++]; var left = x >= 4 ? pixels[row + x - 4] : 0; var up = y > 0 ? pixels[prior + x] : 0; var ul = y > 0 && x >= 4 ? pixels[prior + x - 4] : 0; pixels[row + x] = filter switch { 0 => value, 1 => (byte)(value + left), 2 => (byte)(value + up), 3 => (byte)(value + (left + up) / 2), 4 => (byte)(value + Paeth(left, up, ul)), _ => throw new InvalidDataException("未対応 PNG filter") }; } }
        return new PixelImage(width, height, pixels);
    }
    static void WritePng(string path, int width, int height, byte[] pixels) { using var raw = new MemoryStream(); for (var y = 0; y < height; y++) { raw.WriteByte(0); raw.Write(pixels, y * width * 4, width * 4); } using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) { var bytes = raw.ToArray(); z.Write(bytes, 0, bytes.Length); } var header = new byte[13]; WriteU32(header, 0, width); WriteU32(header, 4, height); header[8] = 8; header[9] = 6; using var output = File.Create(path); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }); Chunk(output, "IHDR", header); Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", System.Array.Empty<byte>()); }
    static int ReadU32(byte[] x, int p) => (x[p] << 24) | (x[p + 1] << 16) | (x[p + 2] << 8) | x[p + 3]; static void WriteU32(byte[] x, int p, int v) { x[p] = (byte)(v >> 24); x[p + 1] = (byte)(v >> 16); x[p + 2] = (byte)(v >> 8); x[p + 3] = (byte)v; }
    static int Paeth(int a, int b, int c) { var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    static void Chunk(Stream stream, string type, byte[] data) { var name = Encoding.ASCII.GetBytes(type); var length = new byte[4]; WriteU32(length, 0, data.Length); stream.Write(length); stream.Write(name); stream.Write(data); var crcData = name.Concat(data).ToArray(); var crc = new byte[4]; WriteU32(crc, 0, unchecked((int)Crc32(crcData))); stream.Write(crc); }
    static uint Crc32(byte[] data) { uint crc = 0xffffffff; foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320 : 0); } return crc ^ 0xffffffff; }
}
