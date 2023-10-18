using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.PixelFormats;

namespace TerrariaPixelArtGenerator;

public class ColorPalette
{
    public ColorPalette()
    {

    }

    public uint[] TileWallPalette = new uint[16777216];
    public byte[] PaintPalette = new byte[16777216];

    public void Load(ReadOnlySpan<uint> tileWall, ReadOnlySpan<byte> paint)
    {

    }

    public (ushort, ushort, byte) GetValuesFromPalette(int r, int g, int b)
    {
        uint wallTile = TileWallPalette[Util.Pack(r, g, b, 0)];
        byte paint = PaintPalette[Util.Pack(r, g, b, 0)];


        ushort createTile = (ushort)(wallTile >> 16);
        ushort createWall = (ushort)(wallTile & 0xffff);

        return (createTile, createWall, paint);
    }

    public (int, int, int) GetColorPlaceInfo(Rgba32 color)
    {
        if (color.A == 0)
        {
            return (-1, -1, -1);
        }

        byte pr = color.R;
        byte pg = color.G;
        byte pb = color.B;

        (int tileTypeP, int wallTypeP, byte paint) = GetValuesFromPalette(pr, pg, pb);

        if (tileTypeP == ushort.MaxValue)
        {
            tileTypeP = -1;
        }

        if (wallTypeP == ushort.MaxValue)
        {
            wallTypeP = -1;
        }

        return (tileTypeP, wallTypeP, paint);
    }

    public class Util
    {
        public static byte Unpack1(uint p)
        {
            uint pos = p;
            uint x = (pos & 0xff);
            return (byte)x;
        }
        public static byte Unpack2(uint p)
        {
            uint pos = p;
            uint y = (pos & 0xff00) >> 8;
            return (byte)y;
        }
        public static byte Unpack3(uint p)
        {
            uint pos = p;
            uint z = (pos & 0xff0000) >> 16;
            return (byte)z;
        }
        public static byte Unpack4(uint p)
        {
            uint pos = p;
            uint w = (uint)((pos & (0xff << 24)) >> 24);
            return (byte)w;
        }

        public static uint Pack(int x, int y, int z, int w)
        {
            return ((uint)w << 24) | ((uint)z << 16) | ((uint)y << 8) | (uint)x;
        }
    }
}