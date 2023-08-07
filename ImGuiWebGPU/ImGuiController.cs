using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace ImGuiWebGPU;

public class ImGuiController
{
    private readonly WebGPU _webGpu;
    private readonly List<char> _pressed = new();

    private readonly unsafe Device* _device;
    private readonly unsafe Queue* _queue;
    private readonly IWindow _window;
    private readonly IInputContext _input;
    
    private unsafe ShaderModule* _shader;
    private unsafe RenderPipeline* _pipeline;
    private unsafe BindGroup* _group;
    
    private readonly unsafe Buffer* _transformationBuffer;
    private unsafe Buffer* _vtxBuffer;
    private unsafe Buffer* _idxBuffer;

    private unsafe Texture* _texture;
    private unsafe TextureView* _textureView;
    private unsafe Sampler* _sampler;
    
    public unsafe ImGuiController(Device* device, Queue* queue, TextureFormat format, IWindow window)
    {
        _webGpu = WebGPU.GetApi();
        _device = device;
        _queue = queue;
        _window = window;
        _input = window.CreateInput();
        
        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        
        CreateShader();

        _transformationBuffer = _webGpu.DeviceCreateBuffer(
                device,
                new BufferDescriptor
                {
                    Size = (ulong)sizeof(Transformation),
                    Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
                    MappedAtCreation = false
                });
        
        var layout = CreateBindGroupLayout();
        CreateFont();
        CreateBindGroup(layout);
        CreatePipeline(format, layout);
        BindInput();
    }

    private unsafe void CreatePipeline(TextureFormat format, BindGroupLayout* layout)
    {
        var blendState = new BlendState
        {
            Color = new BlendComponent
            {
                SrcFactor = BlendFactor.SrcAlpha,
                DstFactor = BlendFactor.OneMinusSrcAlpha,
                Operation = BlendOperation.Add
            },
            Alpha = new BlendComponent
            {
                SrcFactor = BlendFactor.One,
                DstFactor = BlendFactor.OneMinusSrcAlpha,
                Operation = BlendOperation.Add
            }
        };

        var colorTargetState = new ColorTargetState
        {
            Format = format,
            Blend = &blendState,
            WriteMask = ColorWriteMask.All
        };

        var bindGroupLayouts = stackalloc BindGroupLayout*[1];
        bindGroupLayouts[0] = layout;

        var pipelineLayout = _webGpu.DeviceCreatePipelineLayout(
            _device,
            new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 1,
                BindGroupLayouts = bindGroupLayouts
            });

        var vertexAttributes = stackalloc VertexAttribute[3];

        vertexAttributes[0] = new VertexAttribute
        {
            Format = VertexFormat.Float32x2,
            Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.pos)),
            ShaderLocation = 0
        };
        vertexAttributes[1] = new VertexAttribute
        {
            Format = VertexFormat.Float32x3,
            Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.uv)),
            ShaderLocation = 1
        };
        vertexAttributes[2] = new VertexAttribute
        {
            Format = VertexFormat.Unorm8x4,
            Offset = (ulong)Marshal.OffsetOf<ImDrawVert>(nameof(ImDrawVert.col)),
            ShaderLocation = 2
        };

        var vertexBufferLayout = new VertexBufferLayout
        {
            Attributes = vertexAttributes,
            AttributeCount = 3,
            StepMode = VertexStepMode.Vertex,
            ArrayStride = (ulong)sizeof(ImDrawVert)
        };

        var fragmentState = new FragmentState
        {
            Module = _shader,
            TargetCount = 1,
            Targets = &colorTargetState,
            EntryPoint = (byte*)SilkMarshal.StringToPtr("fs_main")
        };

        var vertexState = new VertexState
        {
            Module = _shader,
            EntryPoint = (byte*)SilkMarshal.StringToPtr("vs_main"),
            Buffers = &vertexBufferLayout,
            BufferCount = 1
        };

        var renderPipelineDescriptor = new RenderPipelineDescriptor
        {
            Vertex = vertexState,
            Primitive = new PrimitiveState
            {
                Topology = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace = FrontFace.Ccw,
                CullMode = CullMode.None
            },
            Multisample = new MultisampleState
            {
                Count = 1,
                Mask = ~0u,
                AlphaToCoverageEnabled = false
            },
            Fragment = &fragmentState,
            Layout = pipelineLayout
        };

        _pipeline = _webGpu.DeviceCreateRenderPipeline(_device, renderPipelineDescriptor);
    }

    private unsafe void CreateShader()
    {
        var assembly = typeof(ImGuiController).Assembly;
        var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + "." + "Shaders.ImGui.wgsl");

        var bytes = new byte[stream!.Length];
        _ = stream.Read(bytes, 0, bytes.Length);

        fixed (byte* ptr = bytes)
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Code = ptr,
                Chain = new ChainedStruct(sType: SType.ShaderModuleWgslDescriptor)
            };

            var descriptor = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDescriptor
            };

            _shader = _webGpu.DeviceCreateShaderModule(_device, descriptor);
        }
    }

    private unsafe BindGroupLayout* CreateBindGroupLayout()
    {
        var layouts = stackalloc BindGroupLayoutEntry[3];
        layouts[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform
            },
            Visibility = ShaderStage.Vertex
        };
        layouts[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Texture = new TextureBindingLayout
            {
                Multisampled = false,
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D
            },
            Visibility = ShaderStage.Fragment
        };
        layouts[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Sampler = new SamplerBindingLayout
            {
                Type = SamplerBindingType.Filtering
            },
            Visibility = ShaderStage.Fragment
        };
        return _webGpu.DeviceCreateBindGroupLayout(
            _device,
            new BindGroupLayoutDescriptor
            {
                Entries = layouts,
                EntryCount = 3
            });
    }

    private unsafe void CreateBindGroup(BindGroupLayout* layout)
    {
        var groups = stackalloc BindGroupEntry[3];
        groups[0] = new BindGroupEntry
        {
            Binding = 0,
            Buffer = _transformationBuffer,
            Size = (ulong)sizeof(Transformation)
        };
        groups[1] = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _textureView
        };
        groups[2] = new BindGroupEntry
        {
            Binding = 2,
            Sampler = _sampler
        };
        _group = _webGpu.DeviceCreateBindGroup(
            _device,
            new BindGroupDescriptor
            {
                Entries = groups,
                EntryCount = 3,
                Layout = layout
            });
    }

    private unsafe void CreateFont()
    {
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out var bytesPerPixel);
        var bytesSize = (uint)(width * height * bytesPerPixel);
    
        var descriptor = new TextureDescriptor
        {
            Size = new Extent3D((uint)width, (uint)height, 1),
            Format = TextureFormat.Rgba8Unorm,
            Usage = TextureUsage.CopyDst | TextureUsage.TextureBinding,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.Dimension2D
        };
    
        _texture = _webGpu.DeviceCreateTexture(_device, descriptor);
        _textureView = _webGpu.TextureCreateView(_texture, new TextureViewDescriptor
        {
            Format = TextureFormat.Rgba8Unorm,
            Dimension = TextureViewDimension.Dimension2D,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All
        });
    
        _webGpu.QueueWriteTexture(_queue,
            new ImageCopyTexture
            {
                Texture = _texture,
                Aspect = TextureAspect.All,
                Origin = new Origin3D(0, 0, 0)
            },
            pixels,
            bytesSize,
            new TextureDataLayout
            {
                BytesPerRow = (uint)(width * bytesPerPixel),
                RowsPerImage = (uint)height
            }, 
            new Extent3D
            {
                Width = (uint)width,
                Height = (uint)height,
                DepthOrArrayLayers = 1
            });
    
        io.Fonts.SetTexID((nint)_textureView);

        _sampler = _webGpu.DeviceCreateSampler(_device, new SamplerDescriptor
        {
            AddressModeU = AddressMode.Repeat,
            AddressModeV = AddressMode.Repeat,
            AddressModeW = AddressMode.Repeat,
            MagFilter = FilterMode.Nearest,
            MinFilter = FilterMode.Nearest,
            MipmapFilter = MipmapFilterMode.Nearest,
            Compare = CompareFunction.Undefined,
            MaxAnisotropy = 1
        });
    }
    
    private void BindInput()
    {
        var io = ImGui.GetIO();
        io.KeyMap[(int)ImGuiKey.Tab] = (int)Key.Tab;
        io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)Key.Left;
        io.KeyMap[(int)ImGuiKey.RightArrow] = (int)Key.Right;
        io.KeyMap[(int)ImGuiKey.UpArrow] = (int)Key.Up;
        io.KeyMap[(int)ImGuiKey.DownArrow] = (int)Key.Down;
        io.KeyMap[(int)ImGuiKey.PageUp] = (int)Key.PageUp;
        io.KeyMap[(int)ImGuiKey.PageDown] = (int)Key.PageDown;
        io.KeyMap[(int)ImGuiKey.Home] = (int)Key.Home;
        io.KeyMap[(int)ImGuiKey.End] = (int)Key.End;
        io.KeyMap[(int)ImGuiKey.Delete] = (int)Key.Delete;
        io.KeyMap[(int)ImGuiKey.Backspace] = (int)Key.Backspace;
        io.KeyMap[(int)ImGuiKey.Enter] = (int)Key.Enter;
        io.KeyMap[(int)ImGuiKey.Escape] = (int)Key.Escape;
        io.KeyMap[(int)ImGuiKey.A] = (int)Key.A;
        io.KeyMap[(int)ImGuiKey.C] = (int)Key.C;
        io.KeyMap[(int)ImGuiKey.V] = (int)Key.V;
        io.KeyMap[(int)ImGuiKey.X] = (int)Key.X;
        io.KeyMap[(int)ImGuiKey.Y] = (int)Key.Y;
        io.KeyMap[(int)ImGuiKey.Z] = (int)Key.Z;
        
        var keyboard = _input.Keyboards[0];
        keyboard.KeyChar += OnKeyChar;
    }

    private void UpdateInput()
    {
        var io = ImGui.GetIO();
        
        var mouse = _input.Mice[0];
        var keyboard = _input.Keyboards[0];
        
        io.MouseDown[0] = mouse.IsButtonPressed(MouseButton.Left);
        io.MouseDown[1] = mouse.IsButtonPressed(MouseButton.Right);
        io.MouseDown[2] = mouse.IsButtonPressed(MouseButton.Middle);
        
        var point = new Point((int)mouse.Position.X, (int)mouse.Position.Y);
        io.MousePos = new Vector2(point.X, point.Y);
        
        var wheel = mouse.ScrollWheels[0];
        io.MouseWheel = wheel.Y;
        io.MouseWheelH = wheel.X;
        
        foreach (Key key in Enum.GetValues(typeof(Key)))
        {
            if (key == Key.Unknown) continue;
            io.KeysDown[(int)key] = keyboard.IsKeyPressed(key);
        }
        
        foreach (var c in _pressed)
            io.AddInputCharacter(c);

        _pressed.Clear();
        
        io.KeyCtrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        io.KeyAlt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
        io.KeyShift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        io.KeySuper = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
    }

    public void Update(double delta)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_window.Size.X, _window.Size.Y);
        io.DisplayFramebufferScale = new Vector2(1, 1);
        io.DeltaTime = (float)delta;
        ImGui.NewFrame();
        ImGui.ShowDemoWindow();
        UpdateInput();
    }
    
    public unsafe void Render(RenderPassEncoder* renderPass)
    {
        _webGpu.RenderPassEncoderSetPipeline(renderPass, _pipeline);
        _webGpu.RenderPassEncoderSetBindGroup(renderPass, 0, _group, 0, null);
        
        ImGui.Render();
        
        var drawData = ImGui.GetDrawData();

        var vtxBufferDescriptor = new BufferDescriptor
        {
            Size = (ulong)RoundUp(drawData.TotalVtxCount * sizeof(ImDrawVert), 4), // Round up - Buffers that are mapped at creation have to be aligned to `COPY_BUFFER_ALIGNMENT`
            // Size = (ulong)(drawData.TotalVtxCount * sizeof(ImDrawVert)),
            Usage = BufferUsage.CopyDst | BufferUsage.Vertex,
            MappedAtCreation = true
        };
        _vtxBuffer = _webGpu.DeviceCreateBuffer(_device, vtxBufferDescriptor);
        var vtxBufferPtr = (ImDrawVert*)_webGpu.BufferGetMappedRange(_vtxBuffer, 0, (uint)vtxBufferDescriptor.Size);
            
        var idxBufferDescriptor = new BufferDescriptor
        {
            Size = (ulong)RoundUp(drawData.TotalIdxCount * sizeof(ushort), 4), // Round up - Buffers that are mapped at creation have to be aligned to `COPY_BUFFER_ALIGNMENT`
            // Size = (ulong)drawData.TotalIdxCount * sizeof(ushort),
            Usage = BufferUsage.CopyDst | BufferUsage.Index,
            MappedAtCreation = true
        };
        _idxBuffer = _webGpu.DeviceCreateBuffer(_device, idxBufferDescriptor);
        var idxBufferPtr = (ushort*)_webGpu.BufferGetMappedRange(_idxBuffer, 0, (uint)idxBufferDescriptor.Size);

        if (drawData.TotalVtxCount > 0)
        {
            for (var l = 0; l < drawData.CmdListsCount; l++)
            {
                var cmdList = drawData.CmdListsRange[l];
                
                var vtxSize = cmdList.VtxBuffer.Size * sizeof(ImDrawVert);
                var idxSize = cmdList.IdxBuffer.Size * sizeof(ushort);
                
                Unsafe.CopyBlock(vtxBufferPtr, (void*)cmdList.VtxBuffer.Data, (uint)vtxSize);
                Unsafe.CopyBlock(idxBufferPtr, (void*)cmdList.IdxBuffer.Data, (uint)idxSize);

                vtxBufferPtr += vtxSize;
                idxBufferPtr += idxSize;
            }

            _webGpu.BufferUnmap(_vtxBuffer);
            _webGpu.BufferUnmap(_idxBuffer);
            
            _webGpu.RenderPassEncoderSetVertexBuffer(renderPass, 0, _vtxBuffer, 0, (uint)vtxBufferDescriptor.Size);
            _webGpu.RenderPassEncoderSetIndexBuffer(renderPass, _idxBuffer, IndexFormat.Uint16, 0, (uint)idxBufferDescriptor.Size);
        }
            
        var scale = new Vector2(2.0f / drawData.DisplaySize.X, 2.0f / drawData.DisplaySize.Y);
        var position = new Vector2(-1.0f - drawData.DisplayPos.X * scale.X, -1.0f - drawData.DisplayPos.Y * scale.Y);

        var transformation = new Transformation
        {
            Position = position,
            Scale = scale
        };
        _webGpu.QueueWriteBuffer(_queue, _transformationBuffer, 0, &transformation, (nuint)sizeof(Transformation));
        
        var clipOffset = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;
        
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var l = 0; l < drawData.CmdListsCount; l++)
        {
            var cmdList = drawData.CmdListsRange[l];
            for (var c = 0; c < cmdList.CmdBuffer.Size; c++)
            {
                var cmd = cmdList.CmdBuffer[c];
                
                var clipRect = new Vector4
                {
                    X = (cmd.ClipRect.X - clipOffset.X) * clipScale.X,
                    Y = (cmd.ClipRect.Y - clipOffset.Y) * clipScale.Y,
                    Z = (cmd.ClipRect.Z - clipOffset.X) * clipScale.X,
                    W = (cmd.ClipRect.W - clipOffset.Y) * clipScale.Y
                };

                if (clipRect.X >= _window.FramebufferSize.X || clipRect.Y >= _window.FramebufferSize.Y || clipRect.Z < 0.0f || clipRect.W < 0.0f)  continue;
                
                if (clipRect.X < 0.0f) clipRect.X = 0.0f;
                if (clipRect.Y < 0.0f) clipRect.Y = 0.0f;
                    
                _webGpu.RenderPassEncoderSetScissorRect(renderPass, (uint)clipRect.X, (uint)clipRect.Y, (uint)(clipRect.Z - clipRect.X), (uint)(clipRect.W - clipRect.Y));
                
                _webGpu.RenderPassEncoderDrawIndexed(renderPass, cmd.ElemCount, 1, cmd.IdxOffset + (uint)indexOffset, (int)cmd.VtxOffset + vertexOffset, 0);
            }
            
            indexOffset += cmdList.IdxBuffer.Size;
            vertexOffset += cmdList.VtxBuffer.Size;
        }
    }

    private static int RoundUp(int numToRound, int multiple)
    {
        if (multiple == 0)
            return numToRound;
    
        var remainder = numToRound % multiple;
        if (remainder == 0)
            return numToRound;
    
        return numToRound + multiple - remainder;
    }

    public unsafe void ReleaseBuffers()
    {
        _webGpu.BufferRelease(_vtxBuffer);
        _webGpu.BufferRelease(_idxBuffer);
    }
    
    private void OnKeyChar(IKeyboard _, char key) => _pressed.Add(key);
    
    private struct Transformation
    {
        public Vector2 Position;
        public Vector2 Scale;
    }
}