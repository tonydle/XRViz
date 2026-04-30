Shader "RealityStream/PointCloudSquaresScaled"
{
    Properties
    {
		_SizeScale("Size Scale (factor)", range(0, 1)) = 0.01
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
            StructuredBuffer<float> distanceToOrigin;

            float _SizeScale;

            // vertex shader outputs ("vertex to geometry")
            struct v2g
            {
				float4 pos : SV_POSITION;
                fixed4 color : SV_Target;
                float size : TEXCOORD0;

                UNITY_VERTEX_OUTPUT_STEREO // stereo rendering for VR
			};

            // geometry shader outputs ("geometry to fragment")
            struct g2f
            {
                
                float4 pos : SV_POSITION;
                float3 norm : NORMAL;
                fixed4 color : SV_Target;
            };

            // vertex shader outputs ("vertex to geometry")
            v2g vert(uint vertex_id : SV_VertexID)
            {
                v2g output;

                UNITY_SETUP_INSTANCE_ID(vertex_id);
                UNITY_INITIALIZE_OUTPUT(v2g, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 pos = vertexPosition[vertex_id];
                fixed4 color = (fixed4)vertexColor[vertex_id];
                float distance = distanceToOrigin[vertex_id];

                output.pos = float4(pos, 1);
                output.color = color;
                output.size = _SizeScale * distance;  // Set the size based on distance and _SizeScale
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

                float size = points[0].size;

                const int vertexCount = 4;

                g2f v[vertexCount] = {
                    createGSOut(), createGSOut(), createGSOut(), createGSOut()
                };

                v[0].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(-size/2, -size/2, 0.0, 0.0));
                v[1].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(size/2, -size/2, 0.0, 0.0));
                v[2].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(-size/2, size/2, 0.0, 0.0));
                v[3].pos = mul(UNITY_MATRIX_P, mul(UNITY_MATRIX_MV, center) + float4(size/2, size/2, 0.0, 0.0));

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