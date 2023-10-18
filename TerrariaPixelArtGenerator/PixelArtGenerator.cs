using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using plane;
using plane.Diagnostics;
using plane.Graphics;
using plane.Graphics.Buffers;
using plane.Graphics.Shaders;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using SixLabors.ImageSharp;
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

    private int TileWidth = 32;

    private int TileHeight = 32;

    private bool MaintainAspectRatio = true;

    private PixelImage PixelImage = new PixelImage();

    private Image<Rgba32>? TileTextures;

    private List<ImageSection> RenderedSections = new List<ImageSection>();

    private Vector2 ViewOffset = Vector2.Zero;

    private Vector2 ViewScale = Vector2.One;

    private Matrix4x4 ViewMatrix => Matrix4x4.CreateTranslation(-ViewOffset.X, -ViewOffset.Y, 0) * Matrix4x4.CreateScale(ViewScale.X, ViewScale.Y, 1f);

    private Vector2 MouseTranslateOrigin = Vector2.Zero;

    private bool Panning = false;

    private ImageConversionResult? CurrentResult;

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

        TileTextures = Image.Load<Rgba32>(Path.Combine(path, "Assets", "TileData", "TileTextures.png"));

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
                    if (File.Exists(ImagePath) && TryLoad(ImagePath, out Image<Rgba32>? image))
                    {
                        PixelImage.Load(image);

                        TryGeneratePalette();

                        CheckSizeWidth();

                        ProcessImage();
                    }
                }

                if (ImGui.SliderInt("Width", ref TileWidth, 1, 2048))
                {
                    CheckSizeWidth();
                }

                if (ImGui.SliderInt("Height", ref TileHeight, 1, 2048))
                {
                    CheckSizeHeight();
                }

                if (ImGui.BeginChild("ViewWindow"))
                {
                    ImDrawListPtr drawList = ImGui.GetWindowDrawList();

                    Vector2 windowMin = drawList.GetClipRectMin();
                    Vector2 windowMax = drawList.GetClipRectMax();

                    ImGuiIOPtr io = ImGui.GetIO();

                    bool inWindow = new Rectangle((int)windowMin.X, (int)windowMin.Y, (int)(windowMax.X - windowMin.X), (int)(windowMax.Y - windowMin.Y)).Contains((int)io.MousePos.X, (int)io.MousePos.Y);

                    if (!io.AppFocusLost)
                    {
                        Vector2 originalScaledMouse = io.MousePos / ViewScale;

                        if (inWindow)
                        {
                            ViewScale += new Vector2(io.MouseWheel) * (ViewScale / 10f);
                            ViewScale = Vector2.Clamp(ViewScale, new Vector2(0.01f, 0.01f), new Vector2(20f, 20f));
                        }

                        Vector2 currentScaledMouse = io.MousePos / ViewScale;

                        // Zoom in on where the mouse is
                        ViewOffset += originalScaledMouse - currentScaledMouse;

                        if (inWindow)
                        {
                            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            {
                                MouseTranslateOrigin = io.MousePos / ViewScale + ViewOffset;

                                Panning = true;
                            }
                        }

                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            if (Panning)
                            {
                                Vector2 currentMousePosition = io.MousePos / ViewScale + ViewOffset;

                                ViewOffset += MouseTranslateOrigin - currentMousePosition;
                            }
                        }
                        else
                        {
                            Panning = false;
                        }
                    }


                    Matrix4x4 matrix = ViewMatrix;

                    foreach (ImageSection section in RenderedSections)
                    {
                        drawList.AddImage(section.TextureId, Vector2.Transform(section.Position, matrix), Vector2.Transform(section.Position + section.Size, matrix));
                    }

                    ImGui.EndChild();
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

    private static bool TryLoad(string path, [NotNullWhen(true)] out Image<Rgba32>? image)
    {
        image = null;

        try
        {
            image = Image.Load<Rgba32>(path);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CheckSizeWidth()
    {
        if (MaintainAspectRatio)
        {
            if (PixelImage.RawPixels == null)
            {
                TileHeight = TileWidth;
            }
            else
            {
                float ratio = (float)PixelImage.Height / (float)PixelImage.Width;
                TileHeight = (int)((float)TileWidth * ratio);
            }
        }
    }

    private void CheckSizeHeight()
    {
        if (MaintainAspectRatio)
        {
            if (PixelImage.RawPixels == null)
            {
                TileWidth = TileHeight;
            }
            else
            {
                float ratio = (float)PixelImage.Width / (float)PixelImage.Height;
                TileWidth = (int)((float)TileHeight * ratio);
            }
        }
    }

    private void ProcessImage()
    {
        const int ImageSize = 4096;

        RenderedSections.Clear();

        Logger.WriteLine("Converting Image");

        Stopwatch sw = Stopwatch.StartNew();

        ImageConversionResult result = Convert();

        sw.Stop();

        Logger.WriteLine($"Converted Image in {sw.Elapsed.TotalSeconds}s");

        int sectionsX = (int)Math.Ceiling((TileWidth * 16d) / ImageSize);
        int sectionsY = (int)Math.Ceiling((TileHeight * 16d) / ImageSize);

        Logger.WriteLine("Rendering Tiles");

        sw = Stopwatch.StartNew();

        Parallel.For(0, sectionsX, x =>
        {
            for (int y = 0; y < sectionsY; y++)
            {
                Texture2D texture = RenderSectionToTexture(x, y, ImageSize, ImageSize, result);

                unsafe
                {
                    RenderedSections.Add(new ImageSection((nint)texture.ShaderResourceView.Handle, new Vector2(x, y) * ImageSize, new Vector2(ImageSize)));
                }
            }
        });

        sw.Stop();

        Logger.WriteLine($"Rendered Tiles in {sw.Elapsed.TotalSeconds}s");

        CurrentResult = result;
    }

    private ImageConversionResult Convert()
    {
        ImageConversionResult result = new ImageConversionResult(TileWidth, TileHeight);

        for (int x = 0; x < TileWidth; x++)
        {
            for (int y = 0; y < TileHeight; y++)
            {
                Rgba32 col = PixelImage.InterpolatedSample(x, y, TileWidth, TileHeight);

                if (col.A == 0)
                {
                    result.Walls[x, y] = ushort.MaxValue;
                    result.Tiles[x, y] = ushort.MaxValue;

                    continue;
                }

                (ushort tileType, ushort wallType, byte paintType) = ColorPalette!.GetValuesFromPalette(col.R, col.G, col.B);

                result.Tiles[x, y] = tileType;
                result.Walls[x, y] = wallType;
                result.Paints[x, y] = paintType;
            }
        }

        return result;
    }

    private Texture2D RenderSectionToTexture(int sectionX, int sectionY, int width, int height, ImageConversionResult result)
    {
        using Image<Rgba32> image = new Image<Rgba32>(width, height);

        int tileWidth = width / 16;
        int tileHeight = height / 16;

        // Starting X and Y tiles
        int startX = sectionX * tileWidth;
        int startY = sectionY * tileHeight;

        for (int x = startX; x < Math.Min(startX + tileWidth, result.Width); x++)
        {
            for (int y = startY; y < Math.Min(startY + tileHeight, result.Height); y++)
            {
                ushort tileType = result.Tiles[x, y];

                if (tileType == ushort.MaxValue)
                {
                    continue;
                }

                int rx = x - startX;
                int ry = y - startY;

                int ix = (tileType * 16) % 2048;
                int iy = (tileType / 128) * 16;

                for (int z = 0; z < 16; z++)
                {
                    for (int w = 0; w < 16; w++)
                    {
                        image[rx * 16 + z, ry * 16 + w] = TileTextures![z + ix, w + iy];
                    }
                }
            }
        }

        return new Texture2D(Renderer!, image);
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

public class ImageConversionResult
{
    public int Width;

    public int Height;

    public ushort[,] Tiles;

    public ushort[,] Walls;

    public ushort[,] Items;

    public ushort[,] PaintItems;

    public byte[,] Paints;

    public ImageConversionResult(int width, int height)
    {
        Width = width;

        Height = height;

        Tiles = new ushort[width, height];

        Walls = new ushort[width, height];

        Items = new ushort[width, height];

        PaintItems = new ushort[width, height];

        Paints = new byte[width, height];
    }
}

public record struct ImageSection(nint TextureId, Vector2 Position, Vector2 Size);