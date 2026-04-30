Shader "RealityStream/PointCloudPixels"
{
    SubShader
    {
        Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"}
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            StructuredBuffer<float3> vertexPosition;
            StructuredBuffer<float4> vertexColor;

            // vertex shader outputs ("vertex to fragment")
            struct v2f
            {
                float4 color : SV_Target;
                float4 pos : SV_POSITION;
            };

            v2f vert(uint vertex_id: SV_VertexID)
            {
                v2f o;
                float3 position = vertexPosition[vertex_id];
                fixed4 color = (fixed4)vertexColor[vertex_id];

                o.color = color;
                o.pos = mul(UNITY_MATRIX_VP, float4(position, 1));
                return o;
            } 
            
            float4 frag(v2f i) : SV_TARGET
            {
                return i.color;
            }
            ENDCG
        }
    }
}