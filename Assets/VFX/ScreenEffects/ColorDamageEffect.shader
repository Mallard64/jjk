Shader "Hidden/Resonance/ColorDamageEffect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _FlashColor ("Flash Color", Color) = (1, 0, 0, 0)
        _FlashAmount ("Flash Amount", Range(0, 1)) = 0
        _Invert ("Invert", Range(0, 1)) = 0
        _Glow ("White Glow", Range(0, 1)) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _FlashColor;
            float _FlashAmount;
            float _Invert;
            float _Glow;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                fixed3 inverted = 1.0 - col.rgb;
                col.rgb = lerp(col.rgb, inverted, saturate(_Invert));
                col.rgb = lerp(col.rgb, _FlashColor.rgb, saturate(_FlashAmount));
                col.rgb = lerp(col.rgb, fixed3(1.0, 1.0, 1.0), saturate(_Glow));  // sustained white glow (overdrive)
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
