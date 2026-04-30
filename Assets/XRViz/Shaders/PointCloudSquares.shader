Shader "RealityStream/PointCloudSquares"
{
    Properties
    {
		_Size("Size of Squares (m)", range(0, 0.05)) = 0.01
	}
    SubShader
    {
        Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"}
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma geometry geom
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            StructuredBuffer<float3> vertexPosition;
            StructuredBuffer<float4> vertexColor;

            float _Size;

            // vertex shader outputs ("vertex to geometry")
            struct v2g
            {
				float4 pos : SV_POSITION;
                fixed4 color : SV_Target;
			};

            // geometry shader outputs ("geometry to fragment")
            struct g2f
            {
                
                float4 pos : SV_POSITION;
                float3 norm : NORMAL;
                fixed4 color : SV_Target;
            };

            v2g vert(uint vertex_id: SV_VertexID)
            {
                v2g output;
                float3 pos = vertexPosition[vertex_id];
                fixed4 color = (fixed4)vertexColor[vertex_id];
                
                output.pos = float4(pos,1);
                output.color = color;
                return output;
            } 

            g2f createGSOut()
            {
                g2f output;

                output.pos = float4(0, 0, 0, 0);
                output.norm = float3(0, 0, 0);
                output.color = fixed4(1, 1, 1, 0);

                return output;
            }

            [maxvertexcount(6)]
            void geom(point v2g points[1], inout TriangleStream<g2f> triStream)
            {
                float4 center = points[0].pos;
                fixed4 color = points[0].color;

                const int vertexCount = 4;

                g2f v[vertexCount] = {
                    createGSOut(), createGSOut(), createGSOut(), createGSOut()
                };

                v[0].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(-_Size/2, -_Size/2, 0.0, 0.0));
                v[1].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(_Size/2, -_Size/2, 0.0, 0.0));
                v[2].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(-_Size/2, _Size/2, 0.0, 0.0));
                v[3].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(_Size/2, _Size/2, 0.0, 0.0));

                for (int i = 0; i < vertexCount; i++)
                {
                    v[i].norm = float3(0, 0, 1);
                    v[i].color = color;
                }

                for (int p = 0; p < (vertexCount - 2); p++) {
                    triStream.Append(v[p]);
                    triStream.Append(v[p + 2]);
                    triStream.Append(v[p + 1]);
                }
            }
            
            half4 frag(g2f i) : COLOR
            {
                return float4(i.color);
            }
            ENDCG
        }
    }
}