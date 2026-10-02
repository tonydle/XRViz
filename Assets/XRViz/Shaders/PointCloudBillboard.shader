// Draws a compute-buffer point cloud as camera-facing squares.
//
// Written to replace PointCloudSquares.shader for Quest, which fails there on two counts:
//   1. No UNITY_VERTEX_OUTPUT_STEREO, so under single-pass instanced rendering - which Quest
//      uses by default on OpenXR - the cloud draws to one eye only.
//   2. A geometry shader. Adreno has no native geometry stage; the driver emulates it, and on
//      a 300k-point cloud that emulation is the whole frame budget.
//
// Instead each point is expanded to six vertices in the vertex shader from SV_VertexID: issue
// pointCount*6 vertices as MeshTopology.Triangles and index the buffers with vid/6. Same
// result, no geometry stage, and stereo works because it is an ordinary vertex shader.
//
// Positions are already in world space - the reconstruction compute shader bakes the
// visualiser's localToWorldMatrix in - so the expansion works from UNITY_MATRIX_V, not MVP.
Shader "XRViz/PointCloudBillboard"
{
    Properties
    {
        _Size ("Point size (m)", Range(0.001, 0.1)) = 0.01
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            StructuredBuffer<float3> vertexPosition;
            StructuredBuffer<float4> vertexColor;

            float _Size;

            struct appdata
            {
                uint vid : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Two triangles, counter-clockwise. Cull is off anyway, since a billboard has no
            // meaningful facing and getting the winding wrong would silently halve the cloud.
            static const float2 k_Corners[6] =
            {
                float2(-1, -1), float2( 1, -1), float2(-1,  1),
                float2( 1, -1), float2( 1,  1), float2(-1,  1)
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                uint pointIndex = v.vid / 6;
                uint corner = v.vid % 6;

                float4 color = vertexColor[pointIndex];
                o.color = color;

                // Alpha 0 is the compute shader's "no valid depth here" marker. Push the whole
                // quad outside the clip volume so it is discarded before rasterisation - a
                // clip() in the fragment shader would still pay for the fill.
                if (color.a < 0.5)
                {
                    o.pos = float4(2, 2, 2, 1);
                    return o;
                }

                float3 worldPos = vertexPosition[pointIndex];
                float4 viewPos = mul(UNITY_MATRIX_V, float4(worldPos, 1.0));

                // Offset in view space, so the square always faces the eye and keeps a constant
                // size in metres rather than in pixels
                viewPos.xy += k_Corners[corner] * (_Size * 0.5);

                o.pos = mul(UNITY_MATRIX_P, viewPos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    Fallback Off
}
