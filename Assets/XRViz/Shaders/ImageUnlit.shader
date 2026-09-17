// Draws a ROS camera image on a quad in the room.
//
// Unlit, because passthrough MR has no useful scene lighting and a lit shader renders the image
// near-black. Built-in Unlit/Texture would do that much, but not the three corrections every ROS
// image needs, all of which look like a broken camera rather than a wrong setting:
//
//   FLIP    ROS image data starts at the TOP-left; Unity's LoadRawTextureData fills from the
//           BOTTOM-left. RosSubscriberImage deliberately does not flip - texel (x,y) is ROS pixel
//           (x,y), which is what keeps pixel coordinates agreeing with the camera intrinsics for
//           the point cloud. So the flip belongs here, at the one place the image is looked at.
//   BGR     bgr8/bgra8 are as common as rgb8 on ROS, and load into an RGB texture with red and
//           blue swapped - a blue robot on an orange floor.
//   MONO    mono8 loads as R8 and samples as (v,0,0): a pure red image. Broadcasting R to RGB is
//           the difference between a greyscale picture and a red one.
//
// Stereo macros are not optional: without UNITY_VERTEX_OUTPUT_STEREO this draws to one eye only
// under the single-pass instanced rendering Quest uses by default.
Shader "XRViz/ImageUnlit"
{
    Properties
    {
        _MainTex ("Image", 2D) = "black" {}
        [Toggle] _FlipY ("Flip vertically", Float) = 1
        [Toggle] _Bgr ("Swap red and blue", Float) = 0
        [Toggle] _Mono ("Single channel to greyscale", Float) = 0
        // 16UC1 depth samples as millimetres/65535 - about 0.03 at two metres, i.e. black.
        // Gain makes such an image visible without pretending it is a calibrated depth view.
        _Gain ("Gain", Range(1, 64)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        // A window you can walk behind should still be there when you do
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _FlipY;
            float _Bgr;
            float _Mono;
            float _Gain;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                if (_FlipY > 0.5)
                    o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);

                if (_Mono > 0.5)
                    c.rgb = c.r.xxx;
                else if (_Bgr > 0.5)
                    c.rgb = c.bgr;

                c.rgb = saturate(c.rgb * _Gain);
                c.a = 1;
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
