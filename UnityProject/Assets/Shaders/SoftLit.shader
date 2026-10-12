// Flat colour with soft fixed shading, for solid things in the room (the couch and the person) so
// their shapes read in 3D. It needs no scene lights: the light direction is built in (from above,
// a little in front and to the left), plus a gentle sky/floor ambient, a little darkening close to
// the floor (where light is blocked) and a faint rim along the edges facing away from the camera.
Shader "Atmos/SoftLit"
{
    Properties
    {
        _Color ("Color", Color) = (0.5, 0.5, 0.5, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 world : TEXCOORD1;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 l = normalize(float3(-0.35, 0.85, 0.4));
                float direct = saturate(dot(n, l));
                float sky = 0.5 + 0.5 * n.y;
                float nearFloor = saturate(0.6 + 0.8 * i.world.y);   // 0.6 at the floor, full from 0.5 m up
                float3 toCamera = normalize(_WorldSpaceCameraPos - i.world);
                float rim = pow(1 - saturate(dot(n, toCamera)), 3);
                float shade = (0.32 + 0.48 * direct + 0.2 * sky) * nearFloor + 0.18 * rim;
                return fixed4(_Color.rgb * shade, 1);
            }
            ENDCG
        }
    }
}
