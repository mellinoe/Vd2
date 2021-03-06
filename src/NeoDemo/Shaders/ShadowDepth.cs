﻿using System.Numerics;
using ShaderGen;
using static ShaderGen.ShaderBuiltins;

[assembly: ShaderSet("ShadowDepth", "Shaders.ShadowDepth.VS", "Shaders.ShadowDepth.FS")]

namespace Shaders
{
    public class ShadowDepth
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 World;

        [VertexShader]
        public FragmentInput VS(VertexInput input)
        {
            FragmentInput output;
            output.Position = Mul(ViewProjection, Mul(World, new Vector4(input.Position, 1)));
            return output;
        }

        [FragmentShader]
        public void FS(FragmentInput input) { }

        public struct VertexInput
        {
            [PositionSemantic] public Vector3 Position;
            [NormalSemantic] public Vector3 Normal;
            [TextureCoordinateSemantic] public Vector2 TexCoord;
        }

        public struct FragmentInput
        {
            [PositionSemantic]
            public Vector4 Position;
        }
    }
}
