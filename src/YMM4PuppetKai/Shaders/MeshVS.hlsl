// PuppetKai mesh vertex shader. ASCII only (the source is passed to D3DCompiler as an ANSI string).
// Each vertex has the deformed position and the rest position (both in scene coordinates).
// Direct2D puts four float2 values in b0; read them as two float4:
//   toOutput = (sceneToOutputX[0], sceneToOutputX[1], sceneToOutputY[0], sceneToOutputY[1])
//   toInput  = (sceneToInput0X[0], sceneToInput0X[1], sceneToInput0Y[0], sceneToInput0Y[1])
cbuffer Direct2DTransforms : register(b0)
{
    float4 toOutput;
    float4 toInput;
};

struct VSIn
{
    float2 pos : POSITION;
    float2 src : TEXCOORD0;
};

struct VSOut
{
    float4 clip : SV_POSITION;
    float4 scene : SCENE_POSITION;
    float4 uv : TEXCOORD0;
};

VSOut main(VSIn i)
{
    VSOut o;
    o.clip = float4(i.pos.x * toOutput.x + toOutput.y, i.pos.y * toOutput.z + toOutput.w, 0, 1);
    o.scene = float4(i.pos.x, i.pos.y, 0, 1);
    o.uv = float4(i.src.x * toInput.x + toInput.y, i.src.y * toInput.z + toInput.w, toInput.x, toInput.z);
    return o;
}
