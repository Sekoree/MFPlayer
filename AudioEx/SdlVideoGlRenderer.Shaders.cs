namespace AudioEx;

internal sealed partial class SdlVideoGlRenderer
{
    private static string BuildVertexShader() =>
        """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main()
        {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private static string BuildFragmentShader() =>
        """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main()
        {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    private static string BuildYuvFragmentShader() =>
        """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTextureY;
        uniform sampler2D uTextureU;
        uniform sampler2D uTextureV;
        uniform int uPixelFormat;
        out vec4 FragColor;

        vec3 yuvToRgb(float y, float u, float v)
        {
            float r = y + 1.5748 * v;
            float g = y - 0.1873 * u - 0.4681 * v;
            float b = y + 1.8556 * u;
            return clamp(vec3(r, g, b), 0.0, 1.0);
        }

        void main()
        {
            // 1=NV12, 2=planar 8-bit, 3=P010LE, 4=planar 10-bit LSB-packed
            float scale = (uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

            float y = texture(uTextureY, vTexCoord).r * scale;
            float u;
            float v;

            if (uPixelFormat == 1 || uPixelFormat == 3)
            {
                vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                u = uv.r - 0.5;
                v = uv.g - 0.5;
            }
            else
            {
                u = texture(uTextureU, vTexCoord).r * scale - 0.5;
                v = texture(uTextureV, vTexCoord).r * scale - 0.5;
            }

            FragColor = vec4(yuvToRgb(y, u, v), 1.0);
        }
        """;
}

