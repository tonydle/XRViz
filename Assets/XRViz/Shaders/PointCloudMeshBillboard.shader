// Draws a MESH point cloud as camera-facing squares.
//
// The sibling of PointCloudBillboard.shader, and the difference is the whole point of it: that
// one expands points out of compute buffers from SV_VertexID, which means Graphics.DrawProcedural,
// which renders in world space and ignores the GameObject's Transform - so a PlacementHandle
// cannot move it. This one reads an ordinary mesh, so Unity transforms it for free and the cloud
// can be picked up and put down in the room like every other visualisation (see CLAUDE.md).
//
// Each point is four vertices at the SAME object-space position, with the quad corner carried in
// UV0 as (-1,-1)..(1,1) and expanded in view space here. That keeps the mesh static between
// messages - no per-frame billboarding on the CPU - while the squares still face the eye.
//
// Stereo matters as much as on the other one: without the UNITY_VERTEX_OUTPUT_STEREO macros this
// draws to one eye only under the single-pass instanced rendering Quest uses by default.
Shader "XRViz/PointCloudMeshBillboard"
{
    Properties
    {
        _Size ("Point size (m)", Range(0.001, 0.2)) = 0.012
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
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _Size;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;   // quad corner, (-1,-1)..(1,1)
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Object -> view, so the Transform places the cloud, then offset in view space so
                // the square faces the eye and keeps a constant size in metres rather than pixels
                float3 viewPos = UnityObjectToViewPos(v.vertex.xyz);
                viewPos.xy += v.uv * (_Size * 0.5);

                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                o.color = v.color;
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
