#version 330 core
in vec2 fUv;
in vec4 fColor;
in float fTextureIndex;

uniform sampler2D uTextures[2];

out vec4 FragColor;

void main()
{
    int txIdx = int(fTextureIndex);
    FragColor = texture(uTextures[txIdx], fUv) * fColor;
}