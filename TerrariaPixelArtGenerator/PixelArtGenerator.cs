using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using plane;
using plane.Graphics;
using plane.Graphics.Buffers;
using plane.Graphics.Shaders;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using SixLabors.ImageSharp.PixelFormats;
using TerrariaPixelArtGenerator;
using TerrariaPixelArtGenerator.ID;
using Plane = plane.Plane;

namespace PixelArtGenerator;

public class GeneratorPlane : Plane
{
    public ComputeShader? PaletteComputeShader;

    public Vector4[]? TileColors;
    public Vector4[]? WallColors;

    public ShaderResourceBuffer<Vector4>? TileColorBuffer;

    public ShaderResourceBuffer<Vector4>? WallColorBuffer;

    public ShaderResourceBuffer<Vector4>? PaintColorBuffer;

    public ShaderResourceBuffer<int>? TilesForPixelArtBuffer;

    public ShaderResourceBuffer<int>? WallsForPixelArtBuffer;

    public Texture3D? TileWallPaletteTexture;
    public Texture3D? TileWallPaletteStagingTexture;

    public Texture3D? PaintPaletteTexture;
    public Texture3D? PaintPaletteStagingTexture;

    public ConstantBuffer<GeneratorComputeShaderBuffer>? GeneratorConstantBuffer;

    public bool[] IsTileValid = new bool[TileID.Count];

    public bool[] IsWallValid = new bool[WallID.Count];

    public ColorPalette ColorPalette = new ColorPalette();

    private bool PaletteNeedsUpdate = true;

    private string PaletteEditorSearch = "";

    private string ImagePath = "";

    public GeneratorPlane(string windowName)
        : base(windowName)
    {

    }

    public override void Load()
    {
        string path = Path.GetDirectoryName(typeof(GeneratorPlane).Assembly.Location)!;

        PaletteComputeShader = ShaderCompiler.CompileFromFile<ComputeShader>(Renderer!, Path.Combine(path, "Assets", "Shaders", "PaletteComputeShader.hlsl"), "CSMain", ShaderModel.ComputeShader5_0);

        GeneratorConstantBuffer = new ConstantBuffer<GeneratorComputeShaderBuffer>(Renderer!);

        using FileStream tileColorFileStream = new FileStream(Path.Combine(path, "Assets", "TileData", "tileColorInfo.bin"), FileMode.Open);
        using BinaryReader tileColorReader = new BinaryReader(tileColorFileStream);

        TileColors = new Vector4[tileColorReader.ReadInt32()];
        tileColorReader.Read(MemoryMarshal.AsBytes(TileColors.AsSpan()));

        WallColors = new Vector4[tileColorReader.ReadInt32()];
        tileColorReader.Read(MemoryMarshal.AsBytes(WallColors.AsSpan()));

        Vector4[] paintColors = new Vector4[tileColorReader.ReadInt32()];
        tileColorReader.Read(MemoryMarshal.AsBytes(paintColors.AsSpan()));

        {
            using FileStream validTileFileStream = new FileStream(Path.Combine(path, "Assets", "TileData", "validTileWallInfo.bin"), FileMode.Open);
            using BinaryReader validTileReader = new BinaryReader(validTileFileStream);

            int[] a = new int[validTileReader.ReadInt32()];
            validTileReader.Read(MemoryMarshal.AsBytes(a.AsSpan()));

            int[] b = new int[validTileReader.ReadInt32()];
            validTileReader.Read(MemoryMarshal.AsBytes(b.AsSpan()));

            foreach (int x in a)
            {
                IsTileValid[x] = true;
            }

            foreach (int y in b)
            {
                IsWallValid[y] = true;
            }
        }

        (int[] tilesForPixelArt, int[] wallsForPixelArt) = GetValidTilesAndWalls();

        GeneratorConstantBuffer.Data.TilesForPixelArtLength = tilesForPixelArt.Length;
        GeneratorConstantBuffer.Data.WallsForPixelArtLength = wallsForPixelArt.Length;

        GeneratorConstantBuffer.WriteData();

        TileColorBuffer = new ShaderResourceBuffer<Vector4>(Renderer!, TileColors, Format.FormatR32G32B32A32Float);
        WallColorBuffer = new ShaderResourceBuffer<Vector4>(Renderer!, WallColors, Format.FormatR32G32B32A32Float);
        PaintColorBuffer = new ShaderResourceBuffer<Vector4>(Renderer!, paintColors, Format.FormatR32G32B32A32Float);

        TilesForPixelArtBuffer = new ShaderResourceBuffer<int>(Renderer!, tilesForPixelArt, Format.FormatR32Uint);
        WallsForPixelArtBuffer = new ShaderResourceBuffer<int>(Renderer!, wallsForPixelArt, Format.FormatR32Uint);

        TileWallPaletteTexture = new Texture3D(Renderer!, 256, 256, 256, TextureType.None, BindFlag.UnorderedAccess, Format.FormatR32Uint);
        TileWallPaletteStagingTexture = new Texture3D(Renderer!, 256, 256, 256, TextureType.None, BindFlag.None, Format.FormatR32Uint, Usage.Staging, CpuAccessFlag.Read);

        PaintPaletteTexture = new Texture3D(Renderer!, 256, 256, 256, TextureType.None, BindFlag.UnorderedAccess, Format.FormatR8Uint);
        PaintPaletteStagingTexture = new Texture3D(Renderer!, 256, 256, 256, TextureType.None, BindFlag.None, Format.FormatR8Uint, Usage.Staging, CpuAccessFlag.Read);
    }

    public override void RenderImGui()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize((Vector2)Window.Size);
        ImGui.Begin("Test", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);

        if (ImGui.BeginTabBar("MainTabBar"))
        {
            if (ImGui.BeginTabItem("View"))
            {
                ImGui.InputText("Source Image Path", ref ImagePath, 2048);

                if (ImGui.Button("Generate Pixel Art"))
                {
                    Console.WriteLine($"Image Path: {ImagePath}");
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Edit Palette"))
            {
                ImGui.InputText("Search", ref PaletteEditorSearch, 256);

                bool searchEmpty = PaletteEditorSearch.Length == 0;

                if (ImGui.BeginChild("scrolling"))
                {
                    ImDrawListPtr drawList = ImGui.GetWindowDrawList();

                    for (int i = 0; i < IsTileValid.Length; i++)
                    {
                        if (!searchEmpty && !TileID.Names[i].ToLower().Contains(PaletteEditorSearch.ToLower()))
                            continue;

                        ImGui.Checkbox($"       {TileID.Names[i]}", ref IsTileValid[i]);

                        Vector2 min = ImGui.GetItemRectMin();
                        Vector2 max = ImGui.GetItemRectMax();

                        drawList.AddRectFilled(new Vector2(min.X + 30f, min.Y), new Vector2(min.X + 30f + (max.Y - min.Y), max.Y), new Rgba32(TileColors![i]).PackedValue);
                    }

                    for (int i = 0; i < IsWallValid.Length; i++)
                    {
                        if (!searchEmpty && !WallID.Names[i].ToLower().Contains(PaletteEditorSearch.ToLower()))
                            continue;

                        ImGui.Checkbox($"       {WallID.Names[i]}", ref IsWallValid[i]);

                        Vector2 min = ImGui.GetItemRectMin();
                        Vector2 max = ImGui.GetItemRectMax();

                        drawList.AddRectFilled(new Vector2(min.X + 30f, min.Y), new Vector2(min.X + 30f + (max.Y - min.Y), max.Y), new Rgba32(WallColors![i]).PackedValue);
                    }

                    ImGui.EndChild();
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void TryGeneratePalette()
    {
        if (PaletteNeedsUpdate)
        {
            GeneratePalette();

            PaletteNeedsUpdate = false;
        }
    }

    private void GeneratePalette()
    {
        PaletteComputeShader!.Bind();

        unsafe
        {
            TileColorBuffer!.Bind(0, BindTo.ComputeShader);
            WallColorBuffer!.Bind(1, BindTo.ComputeShader);
            PaintColorBuffer!.Bind(2, BindTo.ComputeShader);

            TilesForPixelArtBuffer!.Bind(3, BindTo.ComputeShader);
            WallsForPixelArtBuffer!.Bind(4, BindTo.ComputeShader);

            Renderer!.Context.CSSetUnorderedAccessViews(0, 1, ref TileWallPaletteTexture!.UnorderedAccessView, (uint*)null);
            Renderer!.Context.CSSetUnorderedAccessViews(1, 1, ref PaintPaletteTexture!.UnorderedAccessView, (uint*)null);
        }

        GeneratorConstantBuffer!.Bind(0, BindTo.ComputeShader);

        Renderer!.Context.Dispatch(26, 26, 26);

        Renderer!.Context.CopyResource(TileWallPaletteStagingTexture!.NativeTexture, TileWallPaletteTexture.NativeTexture);
        Renderer!.Context.CopyResource(PaintPaletteStagingTexture!.NativeTexture, PaintPaletteTexture.NativeTexture);

        ReadOnlySpan<uint> tileWallPaletteData = TileWallPaletteStagingTexture.MapReadSpan<uint>();
        ReadOnlySpan<byte> paintPaletteData = PaintPaletteStagingTexture.MapReadSpan<byte>();

        ColorPalette.Load(tileWallPaletteData, paintPaletteData);

        TileWallPaletteStagingTexture.Unmap();
        PaintPaletteStagingTexture.Unmap();
    }

    private (int[], int[]) GetValidTilesAndWalls()
    {
        int validTileCount = IsTileValid.Count(x => x);
        int validWallCount = IsWallValid.Count(x => x);

        int[] validTiles = new int[validTileCount];
        int[] validWalls = new int[validWallCount];

        int delta0 = 0;
        for (int i = 0; i < IsTileValid.Length; i++)
        {
            if (IsTileValid[i])
            {
                validTiles[delta0++] = i;
            }
        }

        int delta1 = 0;
        for (int i = 0; i < IsWallValid.Length; i++)
        {
            if (IsWallValid[i])
            {
                validWalls[delta1++] = i;
            }
        }

        return (validTiles, validWalls);
    }
}

[StructAlign16]
public partial struct GeneratorComputeShaderBuffer
{
    public int TilesForPixelArtLength;
    public int WallsForPixelArtLength;

    public GeneratorComputeShaderBuffer()
    {
        TilesForPixelArtLength = 0;
        WallsForPixelArtLength = 0;
    }
}