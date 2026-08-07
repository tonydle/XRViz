// Solid-colour shader for the placement handles.
//
// Unlit in the sense that no scene light reaches it: a handle has to stay equally findable
// against a dark lab bench and a sunlit wall, and passthrough MR gives no dependable lighting.
// But a flat unlit sphere reads as a paper disc in stereo, so a fixed top-lit ramp and a rim
// term are baked in purely as shape cues - they are not lighting, and nothing in the scene
// changes them.
//
// Separate from XRViz/VertexColorUnlit because that one multiplies by Mesh.colors, and a
// primitive sphere has no vertex colour channel at all - the tint would depend on whatever the
// platform happens to default the COLOR semantic to.
//
// Includes single-pass instanced stereo support, which Quest uses by default under OpenXR.
// Without the UNITY_VERTEX_* macros the handle draws to only one eye.
Shader "XRViz/HandleUnlit"
{
    Properties
    {
        _Color ("Colour", Color) = (1,1,1,1)
        _RimColor ("Rim Colour", Color) = (1,1,1,1)
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            fixed4 _RimColor;
            float _RimPower;
            float _RimStrength;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.viewDir = _WorldSpaceCameraPos - worldPos;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 v = normalize(i.viewDir);

                half shade = 0.72 + 0.28 * saturate(n.y * 0.5 + 0.5);
                half rim = pow(1.0 - saturate(dot(n, v)), _RimPower);

                fixed3 col = _Color.rgb * shade + _RimColor.rgb * rim * _RimStrength;
                return fixed4(saturate(col), 1);
            }
            ENDCG
        }
    }

    Fallback "Unlit/Color"
}
