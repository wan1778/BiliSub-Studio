using System.Text;

namespace BiliSubStudio.Core.Authentication;

public sealed record QrMatrix(int Size, bool[,] Modules)
{
    public bool At(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && Modules[y, x];
}

public static class QrMatrixEncoder
{
    private const int Version = 10;
    private const int Size = 57;
    private const int DataCodewords = 274;
    private const int EccPerBlock = 18;

    public static QrMatrix Encode(string text)
    {
        var data = MakeData(Encoding.UTF8.GetBytes(text));
        var codewords = Interleave(data);
        var bestScore = int.MaxValue;
        bool[,]? best = null;
        for (var mask = 0; mask < 8; mask++)
        {
            var modules = new bool[Size, Size];
            var function = new bool[Size, Size];
            DrawFunctionPatterns(modules, function);
            DrawFormatBits(modules, function, mask);
            DrawVersionBits(modules, function);
            DrawCodewords(modules, function, codewords, mask);
            var score = Penalty(modules);
            if (score < bestScore) { bestScore = score; best = modules; }
        }
        return new QrMatrix(Size, best ?? throw new InvalidOperationException("Không tạo được QR."));
    }

    private static byte[] MakeData(byte[] source)
    {
        if (source.Length > 271) throw new ArgumentException($"QR URL quá dài: {source.Length} byte (tối đa 271).");
        var bits = new List<bool>(DataCodewords * 8);
        AppendBits(bits, 0x4, 4);
        AppendBits(bits, source.Length, 16);
        foreach (var value in source) AppendBits(bits, value, 8);
        var capacity = DataCodewords * 8;
        for (var i = 0; i < Math.Min(4, capacity - bits.Count); i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);
        var output = new List<byte>(DataCodewords);
        for (var index = 0; index < bits.Count; index += 8)
        {
            byte value = 0;
            for (var bit = 0; bit < 8; bit++) if (bits[index + bit]) value |= (byte)(1 << (7 - bit));
            output.Add(value);
        }
        var initial = output.Count;
        var pads = new byte[] { 0xEC, 0x11 };
        while (output.Count < DataCodewords) output.Add(pads[(output.Count - initial) & 1]);
        return output.ToArray();
    }

    private static void AppendBits(List<bool> bits, int value, int count)
    {
        for (var index = count - 1; index >= 0; index--) bits.Add(((value >> index) & 1) != 0);
    }

    private static byte[] Interleave(byte[] data)
    {
        var lengths = new[] { 68, 68, 69, 69 };
        var blocks = new byte[4][];
        var ecc = new byte[4][];
        var offset = 0;
        for (var index = 0; index < 4; index++)
        {
            blocks[index] = data.Skip(offset).Take(lengths[index]).ToArray();
            offset += lengths[index];
            ecc[index] = ReedSolomon(blocks[index], EccPerBlock);
        }
        var output = new List<byte>(346);
        for (var column = 0; column < 69; column++) for (var block = 0; block < 4; block++) if (column < blocks[block].Length) output.Add(blocks[block][column]);
        for (var column = 0; column < EccPerBlock; column++) for (var block = 0; block < 4; block++) output.Add(ecc[block][column]);
        return output.ToArray();
    }

    private static int GfMultiply(int left, int right)
    {
        var result = 0;
        while (right > 0)
        {
            if ((right & 1) != 0) result ^= left;
            right >>= 1;
            left <<= 1;
            if ((left & 0x100) != 0) left ^= 0x11D;
        }
        return result;
    }

    private static byte[] ReedSolomon(byte[] data, int degree)
    {
        var generator = new int[degree];
        generator[^1] = 1;
        var root = 1;
        for (var index = 0; index < degree; index++)
        {
            for (var item = 0; item < degree; item++)
            {
                generator[item] = GfMultiply(generator[item], root);
                if (item + 1 < degree) generator[item] ^= generator[item + 1];
            }
            root = GfMultiply(root, 2);
        }
        var remainder = new int[degree];
        foreach (var value in data)
        {
            var factor = value ^ remainder[0];
            Array.Copy(remainder, 1, remainder, 0, degree - 1);
            remainder[^1] = 0;
            for (var item = 0; item < degree; item++) remainder[item] ^= GfMultiply(generator[item], factor);
        }
        return remainder.Select(x => (byte)x).ToArray();
    }

    private static void SetFunction(bool[,] modules, bool[,] function, int x, int y, bool dark)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size) return;
        modules[y, x] = dark;
        function[y, x] = true;
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] function)
    {
        for (var index = 0; index < Size; index++)
        {
            SetFunction(modules, function, 6, index, index % 2 == 0);
            SetFunction(modules, function, index, 6, index % 2 == 0);
        }
        DrawFinder(modules, function, 3, 3);
        DrawFinder(modules, function, Size - 4, 3);
        DrawFinder(modules, function, 3, Size - 4);
        foreach (var cy in new[] { 6, 28, 50 }) foreach (var cx in new[] { 6, 28, 50 })
        {
            if ((cx == 6 && (cy == 6 || cy == 50)) || (cy == 6 && cx == 50)) continue;
            DrawAlignment(modules, function, cx, cy);
        }
        for (var index = 0; index < 9; index++) if (index != 6)
        {
            SetFunction(modules, function, 8, index, false);
            SetFunction(modules, function, index, 8, false);
        }
        for (var index = 0; index < 8; index++)
        {
            SetFunction(modules, function, Size - 1 - index, 8, false);
            SetFunction(modules, function, 8, Size - 1 - index, false);
        }
        SetFunction(modules, function, 8, Size - 8, true);
        for (var i = 0; i < 6; i++) for (var j = 0; j < 3; j++)
        {
            SetFunction(modules, function, Size - 11 + j, i, false);
            SetFunction(modules, function, i, Size - 11 + j, false);
        }
    }

    private static void DrawFinder(bool[,] modules, bool[,] function, int centerX, int centerY)
    {
        for (var dy = -4; dy <= 4; dy++) for (var dx = -4; dx <= 4; dx++)
        {
            var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
            SetFunction(modules, function, centerX + dx, centerY + dy, distance != 2 && distance != 4);
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] function, int centerX, int centerY)
    {
        for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
            SetFunction(modules, function, centerX + dx, centerY + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] function, int mask)
    {
        var data = (1 << 3) | mask;
        var remainder = data;
        for (var index = 0; index < 10; index++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        var bits = ((data << 10) | remainder) ^ 0x5412;
        bool Get(int index) => ((bits >> index) & 1) != 0;
        for (var index = 0; index <= 5; index++) SetFunction(modules, function, 8, index, Get(index));
        SetFunction(modules, function, 8, 7, Get(6)); SetFunction(modules, function, 8, 8, Get(7)); SetFunction(modules, function, 7, 8, Get(8));
        for (var index = 9; index < 15; index++) SetFunction(modules, function, 14 - index, 8, Get(index));
        for (var index = 0; index < 8; index++) SetFunction(modules, function, Size - 1 - index, 8, Get(index));
        for (var index = 8; index < 15; index++) SetFunction(modules, function, 8, Size - 15 + index, Get(index));
        SetFunction(modules, function, 8, Size - 8, true);
    }

    private static void DrawVersionBits(bool[,] modules, bool[,] function)
    {
        var remainder = Version;
        for (var index = 0; index < 12; index++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        var bits = (Version << 12) | remainder;
        for (var index = 0; index < 18; index++)
        {
            var dark = ((bits >> index) & 1) != 0;
            var a = Size - 11 + index % 3;
            var b = index / 3;
            SetFunction(modules, function, a, b, dark); SetFunction(modules, function, b, a, dark);
        }
    }

    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => (x * y % 3 + (x + y) % 2) % 2 == 0,
        _ => false,
    };

    private static void DrawCodewords(bool[,] modules, bool[,] function, byte[] code, int mask)
    {
        var bitIndex = 0;
        var upward = true;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right--;
            for (var vertical = 0; vertical < Size; vertical++)
            {
                var y = upward ? Size - 1 - vertical : vertical;
                for (var offset = 0; offset < 2; offset++)
                {
                    var x = right - offset;
                    if (function[y, x]) continue;
                    var dark = bitIndex < code.Length * 8 && ((code[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                    if (bitIndex < code.Length * 8) bitIndex++;
                    if (MaskBit(mask, x, y)) dark = !dark;
                    modules[y, x] = dark;
                }
            }
            upward = !upward;
        }
    }

    private static int Penalty(bool[,] modules)
    {
        var score = 0;
        for (var y = 0; y < Size; y++) score += LinePenalty(Enumerable.Range(0, Size).Select(x => modules[y, x]).ToArray());
        for (var x = 0; x < Size; x++) score += LinePenalty(Enumerable.Range(0, Size).Select(y => modules[y, x]).ToArray());
        for (var y = 0; y < Size - 1; y++) for (var x = 0; x < Size - 1; x++)
            if (modules[y, x] == modules[y, x + 1] && modules[y, x] == modules[y + 1, x] && modules[y, x] == modules[y + 1, x + 1]) score += 3;
        var dark = 0;
        foreach (var value in modules) if ((bool)value) dark++;
        score += Math.Abs(dark * 20 - Size * Size * 10) / (Size * Size) * 10;
        return score;
    }

    private static int LinePenalty(bool[] line)
    {
        var score = 0;
        var color = line[0];
        var length = 1;
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] == color) length++;
            else { if (length >= 5) score += 3 + length - 5; color = line[index]; length = 1; }
        }
        if (length >= 5) score += 3 + length - 5;
        for (var index = 0; index + 10 < line.Length; index++)
        {
            var a = !line[index] && !line[index + 1] && !line[index + 2] && !line[index + 3] && line[index + 4] && !line[index + 5] && line[index + 6] && line[index + 7] && line[index + 8] && !line[index + 9] && line[index + 10];
            var b = line[index] && !line[index + 1] && line[index + 2] && line[index + 3] && line[index + 4] && !line[index + 5] && line[index + 6] && !line[index + 7] && !line[index + 8] && !line[index + 9] && !line[index + 10];
            if (a || b) score += 40;
        }
        return score;
    }
}
