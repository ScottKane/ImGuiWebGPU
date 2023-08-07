// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using Color = Silk.NET.WebGPU.Color;

namespace ImGuiWebGPU;

public static unsafe class Program
{
    private static ImGuiController _imGui = null!;
    
    private static WebGPU   _webGpu = null!;
    private static IWindow? _window;

    private static Instance*       _instance;
    private static Surface*        _surface;
    private static Adapter*        _adapter;
    private static Device*         _device;
    private static ShaderModule*   _shader;
    private static RenderPipeline* _pipeline;
    private static SwapChain*      _swapchain;
    private static TextureFormat   _swapchainFormat;

    private const string Shader = """
                                  @vertex
                                  fn vs_main(@builtin(vertex_index) in_vertex_index: u32) -> @builtin(position) vec4<f32> {
                                      let x = f32(i32(in_vertex_index) - 1);
                                      let y = f32(i32(in_vertex_index & 1u) * 2 - 1);
                                      return vec4<f32>(x, y, 0.0, 1.0);
                                  }

                                  @fragment
                                  fn fs_main() -> @location(0) vec4<f32> {
                                      return vec4<f32>(1.0, 0.0, 0.0, 1.0);
                                  }
                                  """;

    public static void Main()
    {
        var options = WindowOptions.Default;
        options.API                      = GraphicsAPI.None;
        options.FramesPerSecond          = 60;
        options.UpdatesPerSecond         = 60;
        options.Title                    = "WebGPU Triangle";
        options.IsVisible                = true;
        options.ShouldSwapAutomatically  = false;
        options.IsContextControlDisabled = true;

        _window = Window.Create(options);

        _window.Load              += WindowOnLoad;
        _window.Closing           += WindowClosing;
        _window.Update            += WindowOnUpdate;
        _window.Render            += WindowOnRender;
        _window.FramebufferResize += FramebufferResize;

        _window.Run();
    }

    private static void FramebufferResize(Vector2D<int> obj) => CreateSwapchain();

    private static void WindowOnLoad()
    {
        _webGpu = WebGPU.GetApi();

        var instanceDescriptor = new InstanceDescriptor();
        _instance = _webGpu.CreateInstance(&instanceDescriptor);

        _surface = _window.CreateWebGPUSurface(_webGpu, _instance);
        
        var requestAdapterOptions = new RequestAdapterOptions
        {
            CompatibleSurface = _surface
        };

        _webGpu.InstanceRequestAdapter
        (
            _instance,
            requestAdapterOptions,
            new PfnRequestAdapterCallback((_, adapter1, _, _) => _adapter = adapter1),
            null
        );

        Console.WriteLine($"Got adapter {(nuint) _adapter:X}");

        PrintAdapterFeatures();

        var descriptor = new DeviceDescriptor
        {
            DeviceLostCallback = new PfnDeviceLostCallback(DeviceLost),
        };
        _webGpu.AdapterRequestDevice
        (
            _adapter,
            descriptor,
            new PfnRequestDeviceCallback((_, device1, _, _) => _device = device1),
            null
        );
        _webGpu.DeviceSetUncapturedErrorCallback(_device, new PfnErrorCallback(UncapturedError), null);

        Console.WriteLine($"Got device {(nuint) _device:X}");

        _webGpu.DeviceSetUncapturedErrorCallback(_device, new PfnErrorCallback(UncapturedError), null);
        
        var wgslDescriptor = new ShaderModuleWGSLDescriptor
        {
            Code = (byte*) SilkMarshal.StringToPtr(Shader),
            Chain = new ChainedStruct
            {
                SType = SType.ShaderModuleWgslDescriptor
            }
        };

        var shaderModuleDescriptor = new ShaderModuleDescriptor
        {
            NextInChain = (ChainedStruct*) (&wgslDescriptor),
        };

        _shader = _webGpu.DeviceCreateShaderModule(_device, shaderModuleDescriptor);

        Console.WriteLine($"Created shader {(nuint) _shader:X}");

        _swapchainFormat = _webGpu.SurfaceGetPreferredFormat(_surface, _adapter);
        
        var blendState = new BlendState
        {
            Color = new BlendComponent
            {
                SrcFactor = BlendFactor.One,
                DstFactor = BlendFactor.Zero,
                Operation = BlendOperation.Add
            },
            Alpha = new BlendComponent
            {
                SrcFactor = BlendFactor.One,
                DstFactor = BlendFactor.Zero,
                Operation = BlendOperation.Add
            }
        };

        var colorTargetState = new ColorTargetState
        {
            Format    = _swapchainFormat,
            Blend     = &blendState,
            WriteMask = ColorWriteMask.All
        };

        var fragmentState = new FragmentState
        {
            Module      = _shader,
            TargetCount = 1,
            Targets     = &colorTargetState,
            EntryPoint  = (byte*) SilkMarshal.StringToPtr("fs_main")
        };

        var renderPipelineDescriptor = new RenderPipelineDescriptor
        {
            Vertex = new VertexState
            {
                Module     = _shader,
                EntryPoint = (byte*) SilkMarshal.StringToPtr("vs_main"),
            },
            Primitive = new PrimitiveState
            {
                Topology         = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace        = FrontFace.Ccw,
                CullMode         = CullMode.None
            },
            Multisample = new MultisampleState
            {
                Count                  = 1,
                Mask                   = ~0u,
                AlphaToCoverageEnabled = false
            },
            Fragment     = &fragmentState,
            DepthStencil = null
        };

        _pipeline = _webGpu.DeviceCreateRenderPipeline(_device, renderPipelineDescriptor);

        Console.WriteLine($"Created pipeline {(nuint) _pipeline:X}");

        CreateSwapchain();

        _imGui = new ImGuiController(_device, _webGpu.DeviceGetQueue(_device), _swapchainFormat, _window);
    }

    private static void WindowClosing()
    {
        _webGpu.ShaderModuleRelease(_shader);
        _webGpu.RenderPipelineRelease(_pipeline);
        _webGpu.DeviceRelease(_device);
        _webGpu.AdapterRelease(_adapter);
        _webGpu.SurfaceRelease(_surface);
        _webGpu.InstanceRelease(_instance);

        _webGpu.Dispose();
    }

    private static void CreateSwapchain()
    {
        var swapChainDescriptor = new SwapChainDescriptor
        {
            Usage       = TextureUsage.RenderAttachment,
            Format      = _swapchainFormat,
            Width       = (uint) _window.FramebufferSize.X,
            Height      = (uint) _window.FramebufferSize.Y,
            PresentMode = PresentMode.Fifo
        };

        _swapchain = _webGpu.DeviceCreateSwapChain(_device, _surface, swapChainDescriptor);
    }

    private static void WindowOnUpdate(double delta) => _imGui.Update(delta);

    private static void WindowOnRender(double delta)
    {
        TextureView* nextTexture = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            nextTexture = _webGpu.SwapChainGetCurrentTextureView(_swapchain);

            if (attempt == 0 && nextTexture == null)
            {
                Console.WriteLine("wgpu.SwapChainGetCurrentTextureView() failed; trying to create a new swap chain...\n");
                CreateSwapchain();
                continue;
            }

            break;
        }

        if (nextTexture == null)
        {
            Console.WriteLine("wgpu.SwapChainGetCurrentTextureView() failed after multiple attempts; giving up.\n");
            return;
        }

        var commandEncoderDescriptor = new CommandEncoderDescriptor();

        var encoder = _webGpu.DeviceCreateCommandEncoder(_device, commandEncoderDescriptor);

        var colorAttachment = new RenderPassColorAttachment
        {
            View          = nextTexture,
            ResolveTarget = null,
            LoadOp        = LoadOp.Clear,
            StoreOp       = StoreOp.Store,
            ClearValue = new Color
            {
                R = 0,
                G = 0,
                B = 0,
                A = 1
            }
        };

        var renderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachments       = &colorAttachment,
            ColorAttachmentCount   = 1,
            DepthStencilAttachment = null
        };

        var renderPass = _webGpu.CommandEncoderBeginRenderPass(encoder, renderPassDescriptor);

        _webGpu.RenderPassEncoderSetPipeline(renderPass, _pipeline);
        _webGpu.RenderPassEncoderDraw(renderPass, 3, 1, 0, 0);
        
        _imGui.Render(renderPass);
        
        _webGpu.RenderPassEncoderEnd(renderPass);
        _webGpu.TextureViewRelease(nextTexture);
        
        _imGui.ReleaseBuffers();

        var queue = _webGpu.DeviceGetQueue(_device);

        var commandBuffer = _webGpu.CommandEncoderFinish(encoder, new CommandBufferDescriptor());

        _webGpu.QueueSubmit(queue, 1, &commandBuffer);
        _webGpu.SwapChainPresent(_swapchain);
        _window?.SwapBuffers();
    }

    private static void PrintAdapterFeatures()
    {
        var count = (int) _webGpu.AdapterEnumerateFeatures(_adapter, null);

        var features = stackalloc FeatureName[count];

        _webGpu.AdapterEnumerateFeatures(_adapter, features);

        Console.WriteLine("Adapter features:");

        for (var i = 0; i < count; i++)
        {
            Console.WriteLine($"\t{features[i]}");
        }
    }

    private static void DeviceLost(DeviceLostReason arg0, byte* arg1, void* arg2) => Console.WriteLine($"Device lost! Reason: {arg0} Message: {SilkMarshal.PtrToString((nint) arg1)}");

    private static void UncapturedError(ErrorType arg0, byte* arg1, void* arg2) => Console.WriteLine($"{arg0}: {SilkMarshal.PtrToString((nint) arg1)}");
}