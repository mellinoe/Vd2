﻿using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using VdSdl2;

namespace Vd2.NeoDemo.Objects
{
    public class ShadowmapDrawer : Renderable
    {
        private readonly Func<Sdl2Window> _windowGetter;
        private readonly DisposeCollector _disposeCollector = new DisposeCollector();

        private VertexBuffer _vb;
        private IndexBuffer _ib;
        private UniformBuffer _orthographicBuffer;
        private UniformBuffer _sizeInfoBuffer;
        private Pipeline _pipeline;
        private ResourceSet _resourceSet;

        private Vector2 _position;
        private Vector2 _size = new Vector2(100, 100);

        private readonly Func<TextureView> _bindingGetter;
        private SizeInfo? _si;
        private Matrix4x4? _ortho;

        public Vector2 Position { get => _position; set { _position = value; UpdateSizeInfoBuffer(); } }

        public Vector2 Size { get => _size; set { _size = value; UpdateSizeInfoBuffer(); } }

        private void UpdateSizeInfoBuffer()
        {
            _si = new SizeInfo { Size = _size, Position = _position };
        }

        public ShadowmapDrawer(Func<Sdl2Window> windowGetter, Func<TextureView> bindingGetter)
        {
            _windowGetter = windowGetter;
            OnWindowResized();
            _bindingGetter = bindingGetter;
        }

        public void OnWindowResized()
        {
            _ortho = Matrix4x4.CreateOrthographicOffCenter(0, _windowGetter().Width, _windowGetter().Height, 0, -1, 1);
        }

        public override void CreateDeviceObjects(GraphicsDevice gd, CommandList cl, SceneContext sc)
        {
            ResourceFactory factory = gd.ResourceFactory;
            _vb = factory.CreateVertexBuffer(new BufferDescription(s_quadVerts.SizeInBytes()));
            cl.UpdateBuffer(_vb, 0, s_quadVerts);
            _ib = factory.CreateIndexBuffer(new IndexBufferDescription(s_quadIndices.SizeInBytes(), IndexFormat.UInt16));
            cl.UpdateBuffer(_ib, 0, s_quadIndices);

            VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate,  VertexElementFormat.Float2))
            };

            Shader vs = ShaderHelper.LoadShader(factory, "ShadowmapPreviewShader", ShaderStages.Vertex);
            Shader fs = ShaderHelper.LoadShader(factory, "ShadowmapPreviewShader", ShaderStages.Fragment);
            ShaderStageDescription[] shaderStages = new ShaderStageDescription[]
            {
                new ShaderStageDescription(ShaderStages.Vertex, vs, "VS"),
                new ShaderStageDescription(ShaderStages.Fragment, fs, "FS"),
            };

            ResourceLayout layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Projection", ResourceKind.Uniform, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("SizePos", ResourceKind.Uniform, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("Tex", ResourceKind.Texture, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("TexSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            PipelineDescription pd = new PipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, true, DepthComparisonKind.Always),
                RasterizerStateDescription.Default,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, shaderStages),
                layout,
                gd.SwapchainFramebuffer.OutputDescription);

            _pipeline = factory.CreatePipeline(ref pd);

            _sizeInfoBuffer = factory.CreateUniformBuffer(new BufferDescription((uint)Unsafe.SizeOf<SizeInfo>()));
            UpdateSizeInfoBuffer();
            _orthographicBuffer = factory.CreateUniformBuffer(new BufferDescription((uint)Unsafe.SizeOf<Matrix4x4>()));

            _resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                layout,
                _orthographicBuffer,
                _sizeInfoBuffer,
                _bindingGetter(),
                gd.PointSampler));

            OnWindowResized();

            _disposeCollector.Add(_vb, _ib, layout, vs, fs, _pipeline, _sizeInfoBuffer, _orthographicBuffer, _resourceSet);
        }

        public override void DestroyDeviceObjects()
        {
            _disposeCollector.DisposeAll();
        }

        public override RenderOrderKey GetRenderOrderKey(Vector3 cameraPosition)
        {
            return RenderOrderKey.Create(_pipeline.GetHashCode(), 0);
        }

        public override RenderPasses RenderPasses => RenderPasses.Overlay;

        public override void UpdatePerFrameResources(GraphicsDevice gd, CommandList cl, SceneContext sc)
        {
            if (_si.HasValue)
            {
                cl.UpdateBuffer(_sizeInfoBuffer, 0, _si.Value);
                _si = null;
            }

            if (_ortho.HasValue)
            {
                cl.UpdateBuffer(_orthographicBuffer, 0, _ortho.Value);
                _ortho = null;
            }
        }

        public override void Render(GraphicsDevice gd, CommandList cl, SceneContext sc, RenderPasses renderPass)
        {
            cl.SetVertexBuffer(0, _vb);
            cl.SetIndexBuffer(_ib);
            cl.SetPipeline(_pipeline);
            cl.SetResourceSet(_resourceSet);
            cl.Draw((uint)s_quadIndices.Length, 1, 0, 0, 0);
        }

        private static float[] s_quadVerts = new float[]
        {
            0, 0, 0, 0,
            1, 0, 1, 0,
            1, 1, 1, 1,
            0, 1, 0, 1
        };

        private static ushort[] s_quadIndices = new ushort[] { 0, 1, 2, 0, 2, 3 };

        public struct SizeInfo
        {
            public Vector2 Position;
            public Vector2 Size;
        }
    }
}
