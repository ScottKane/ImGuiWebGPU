struct VertexInput {
    @location(0) Position: vec2<f32>,
    @location(1) TextureCoordinate: vec2<f32>,
    @location(2) Colour: vec4<f32>
}

struct VertexOutput {
    @builtin(position) Position: vec4<f32>,
    @location(0) TextureCoordinate: vec2<f32>,
    @location(1) Colour: vec4<f32>
}

struct Transformation {
    Position: vec2<f32>,
    Scale: vec2<f32>
}

@group(0) @binding(0) var<uniform> _transformation: Transformation;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;

    output.Colour = input.Colour;
    output.TextureCoordinate = input.TextureCoordinate;
    output.Position = vec4<f32>((input.Position * _transformation.Scale + _transformation.Position) * vec2<f32>(1.0, -1.0), 0.0, 1.0);
    
    return output;
}

struct FragmentInput {
    @location(0) TextureCoordinate: vec2<f32>,
    @location(1) Colour: vec4<f32>
}

struct FragmentOutput {
    @location(0) Colour: vec4<f32>
}

@group(0) @binding(1) var _texture: texture_2d<f32>;
@group(0) @binding(2) var _sampler: sampler;

@fragment
fn fs_main(input: FragmentInput) -> FragmentOutput {
    var output: FragmentOutput;
    
    output.Colour = input.Colour * textureSample(_texture, _sampler, input.TextureCoordinate);
    output.Colour.r *= output.Colour.a;
    output.Colour.g *= output.Colour.a;
    output.Colour.b *= output.Colour.a;

    return output;
}