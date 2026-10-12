// Converts a decoded video frame (YUV 4:2:0 as two textures: full-size luma, half-size chroma) to
// display RGB. HDR10 (PQ, BT.2020) is tonemapped to SDR the same way the earlier ffmpeg recipe did:
// linear light relative to 100-nit reference white, BT.2020 -> BT.709 primaries, Hable curve on
// the brightest component (keeps hue), BT.709 transfer for display.
//
// _TenBit: P010 (10-bit in the top bits of 16) vs NV12 (8-bit). Limited ("TV") range assumed.

Shader "Atmos/VideoYuv"
{
    Properties
    {
        _YTex ("Luma", 2D) = "black" {}
        _UVTex ("Chroma (Cb, Cr)", 2D) = "gray" {}
        _TenBit ("10-bit samples", Float) = 0
        _Bt2020 ("BT.2020 matrix", Float) = 0
        _Pq ("PQ (HDR10) transfer", Float) = 0
        _PeakNits ("Mastering peak, nits", Float) = 1000
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _YTex, _UVTex;
            float _TenBit, _Bt2020, _Pq, _PeakNits;

            // SMPTE ST 2084 (PQ) EOTF: signal 0..1 -> linear light, 1.0 = 10,000 nits
            float3 PqToLinear(float3 e)
            {
                const float m1 = 0.1593017578125, m2 = 78.84375;
                const float c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
                float3 ep = pow(saturate(e), 1.0 / m2);
                return pow(max(ep - c1, 0.0) / (c2 - c3 * ep), 1.0 / m1);
            }

            float Hable(float x)
            {
                const float a = 0.15, b = 0.50, c = 0.10, d = 0.20, e = 0.02, f = 0.30;
                return (x * (x * a + b * c) + d * e) / (x * (x * a + b) + d * f) - e / f;
            }

            // ITU-R BT.709 OETF (what zscale t=bt709 applied)
            float3 Bt709Oetf(float3 l)
            {
                l = saturate(l);
                return lerp(1.099 * pow(l, 0.45) - 0.099, 4.5 * l, step(l, 0.018));
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = float2(i.uv.x, 1.0 - i.uv.y);   // frames arrive top row first
                float y = tex2D(_YTex, uv).r;
                float2 c = tex2D(_UVTex, uv).rg;

                // back to code values, then limited range to 0..1 (luma) and -0.5..0.5 (chroma)
                float scale = _TenBit > 0.5 ? 65535.0 / 64.0 : 255.0;
                float k = _TenBit > 0.5 ? 4.0 : 1.0;
                float Y = (y * scale - 16.0 * k) / (219.0 * k);
                float Cb = (c.r * scale - 128.0 * k) / (224.0 * k);
                float Cr = (c.g * scale - 128.0 * k) / (224.0 * k);

                float3 rgb = _Bt2020 > 0.5
                    ? float3(Y + 1.4746 * Cr, Y - 0.164553 * Cb - 0.571353 * Cr, Y + 1.8814 * Cb)
                    : float3(Y + 1.5748 * Cr, Y - 0.187324 * Cb - 0.468124 * Cr, Y + 1.8556 * Cb);

                if (_Pq > 0.5)
                {
                    float3 lin = PqToLinear(rgb) * 100.0;   // relative to 100 nits
                    lin = float3(dot(lin, float3(1.660491, -0.587641, -0.072850)),
                                 dot(lin, float3(-0.124550, 1.132900, -0.008349)),
                                 dot(lin, float3(-0.018151, -0.100579, 1.118730)));
                    lin = max(lin, 0.0);
                    float sig = max(max(lin.r, lin.g), max(lin.b, 1e-6));
                    float peak = max(_PeakNits / 100.0, 1.0);
                    lin *= (Hable(sig) / Hable(peak)) / sig;
                    rgb = Bt709Oetf(lin);
                }
                return fixed4(saturate(rgb), 1.0);
            }
            ENDCG
        }
    }
}
