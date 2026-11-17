// PuppetKai mesh pixel shader. ASCII only.
Texture2D InputTexture : register(t0);
SamplerState InputSampler : register(s0);

float4 main(float4 clip : SV_POSITION, float4 scene : SCENE_POSITION, float4 uv : TEXCOORD0) : SV_TARGET
{
    return InputTexture.Sample(InputSampler, uv.xy);
}
