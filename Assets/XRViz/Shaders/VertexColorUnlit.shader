// Unlit shader that renders mesh vertex colours. Used by LaserScanVisualizer, whose per-point
// range gradient is baked into Mesh.colors - the built-in Unlit/Color ignores vertex colours
// entirely, so the gradient would silently render flat white with it.
//
// Unlit on purpose: in passthrough MR there is no useful scene lighting, so a lit shader
// renders visualisations near-black.
//
// Includes single-pass instanced stereo support, which Quest uses by default under OpenXR.
// Without the UNITY_VERTEX_* macros the mesh draws to only one eye or at the wrong offset.
Shader "XRViz/VertexColorUnlit"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
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
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    Fallback "Unlit/Color"
}
