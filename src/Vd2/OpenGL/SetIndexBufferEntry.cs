﻿namespace Vd2.OpenGL
{
    internal class SetIndexBufferEntry : OpenGLCommandEntry
    {
        public IndexBuffer IndexBuffer;

        public SetIndexBufferEntry(IndexBuffer ib)
        {
            IndexBuffer = ib;
        }

        public SetIndexBufferEntry() { }

        public SetIndexBufferEntry Init(IndexBuffer ib)
        {
            IndexBuffer = ib;
            return this;
        }

        public override void ClearReferences()
        {
            IndexBuffer = null;
        }
    }
}