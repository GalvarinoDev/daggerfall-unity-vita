// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// LZ4 block decoder for packs compressed on the PC. Palette atlases shrink to ~13%, and the card
// read was the cost of an atlas load (pal7: 89.7 MB in 6.7 s per Palace route). Native memory in, native memory out: no managed garbage.

#if UNITY_PSP2
public static class VitaLz4
{
    /// <summary>Decodes one LZ4 block of srcLength bytes into exactly dstLength bytes. False on damaged input (never writes past dst).</summary>
    public static unsafe bool Decode(System.IntPtr src, int srcLength, System.IntPtr dst, int dstLength)
    {
        byte* ip = (byte*)src, iend = ip + srcLength;
        byte* op = (byte*)dst, ostart = op, oend = op + dstLength;
        while (ip < iend)
        {
            int token = *ip++;
            int length = token >> 4;
            if (length == 15)
            {
                int b;
                do
                {
                    if (ip >= iend)
                        return false;
                    b = *ip++;
                    length += b;
                } while (b == 255);
            }
            if (length > iend - ip || length > oend - op)
                return false;
            System.Buffer.MemoryCopy(ip, op, length, length);
            ip += length;
            op += length;
            if (ip == iend)
                break;   // the last sequence is literals only

            if (iend - ip < 2)
                return false;
            int offset = ip[0] | (ip[1] << 8);
            ip += 2;
            byte* match = op - offset;
            if (offset == 0 || match < ostart)
                return false;
            length = token & 15;
            if (length == 15)
            {
                int b;
                do
                {
                    if (ip >= iend)
                        return false;
                    b = *ip++;
                    length += b;
                } while (b == 255);
            }
            length += 4;
            if (length > oend - op)
                return false;
            // Overlapping copy (offset < length, e.g. runs of index 0): the copied span doubles each step
            while (length > 0)
            {
                int n = (int)System.Math.Min(length, op - match);
                System.Buffer.MemoryCopy(match, op, n, n);
                op += n;
                length -= n;
            }
        }
        return op == oend;
    }
}
#endif
