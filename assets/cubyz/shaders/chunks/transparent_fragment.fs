#version 430

in vec3 mvVertexPos;
flat in int blockType;
flat in int faceNormal;
flat in int modelIndex;
flat in int isBackFace;
flat in int ditherSeed;
in vec3 startPosition;
in vec3 direction;

uniform int time;
uniform vec3 ambientLight;
uniform sampler2DArray texture_sampler;
uniform sampler2DArray emissionSampler;
uniform samplerCube reflectionMap;
uniform float reflectionMapSize;

layout(binding = 3) uniform sampler2D depthTexture;

layout (location = 0, index = 0) out vec4 fragColor;
layout (location = 0, index = 1) out vec4 blendColor;

struct Fog {
	vec3 color;
	float density;
};

struct AnimationData {
	int frames;
	int time;
};

struct TextureData {
	int textureIndices[6];
	uint absorption;
	float reflectivity;
	float fogDensity;
	uint fogColor;
};

layout(std430, binding = 0) buffer _animation
{
	AnimationData animation[];
};
layout(std430, binding = 1) buffer _textureData
{
	TextureData textureData[];
};


const float[6] normalVariations = float[6](
	1.0, //vec3(0, 1, 0),
	0.84, //vec3(0, -1, 0),
	0.92, //vec3(1, 0, 0),
	0.92, //vec3(-1, 0, 0),
	0.96, //vec3(0, 0, 1),
	0.88 //vec3(0, 0, -1)
);
const vec3[6] normals = vec3[6](
	vec3(0, 1, 0),
	vec3(0, -1, 0),
	vec3(1, 0, 0),
	vec3(-1, 0, 0),
	vec3(0, 0, 1),
	vec3(0, 0, -1)
);

uniform float zNear;
uniform float zFar;

uniform Fog fog;

vec3 unpackColor(uint color) {
	return vec3(
		color>>16 & 255u,
		color>>8 & 255u,
		color & 255u
	)/255.0;
}

float zFromDepth(float depthBufferValue) {
	return zNear*zFar/(depthBufferValue*(zNear - zFar) + zFar);
}

float calculateFogDistance(float depthBufferValue, float fogDensity) {
	float distCameraTerrain = zFromDepth(depthBufferValue)*fogDensity;
	float distFromCamera = abs(mvVertexPos.z)*fogDensity;
	float distFromTerrain = distFromCamera - distCameraTerrain;
	if(distCameraTerrain < 10) { // Resolution range is sufficient.
		return distFromTerrain;
	} else {
		// Here we have a few options to deal with this. We could for example weaken the fog effect to fit the entire range.
		// I decided to keep the fog strength close to the camera and far away, with a fog-free region in between.
		// I decided to this because I want far away fog to work (e.g. a distant ocean) as well as close fog(e.g. the top surface of the water when the player is under it)
		if(distFromTerrain > -5 && depthBufferValue != 0) {
			return distFromTerrain;
		} else if(distFromCamera < 5) {
			return distFromCamera - 10;
		} else {
			return -5;
		}
	}
}

void applyFrontfaceFog(float fogDistance, vec3 fogColor) {
	float fogFactor = exp(fogDistance);
	fragColor.a = 1.0/fogFactor;
	fragColor.rgb = fragColor.a*fogColor;
	fragColor.rgb -= fogColor;
}

void applyBackfaceFog(float fogDistance, vec3 fogColor) {
	float fogFactor = exp(fogDistance);
	float oldAlpha = fragColor.a;
	fragColor.a *= fogFactor;
	fragColor.rgb -= oldAlpha*fogColor;
	fragColor.rgb += fragColor.a*fogColor;
}

vec2 getTextureCoordsNormal(vec3 voxelPosition, int textureDir) {
	switch(textureDir) {
		case 0:
			return vec2(15 - voxelPosition.x, voxelPosition.z);
		case 1:
			return vec2(voxelPosition.x, voxelPosition.z);
		case 2:
			return vec2(15 - voxelPosition.z, voxelPosition.y);
		case 3:
			return vec2(voxelPosition.z, voxelPosition.y);
		case 4:
			return vec2(voxelPosition.x, voxelPosition.y);
		case 5:
			return vec2(15 - voxelPosition.x, voxelPosition.y);
	}
}

vec4 fixedCubeMapLookup(vec3 v) { // Taken from http://the-witness.net/news/2012/02/seamless-cube-map-filtering/
   float M = max(max(abs(v.x), abs(v.y)), abs(v.z));
   float scale = (reflectionMapSize - 1)/reflectionMapSize;
   if (abs(v.x) != M) v.x *= scale;
   if (abs(v.y) != M) v.y *= scale;
   if (abs(v.z) != M) v.z *= scale;
   return texture(reflectionMap, v);
}

void main() {
	int textureIndex = textureData[blockType].textureIndices[faceNormal];
	textureIndex = textureIndex + time / animation[textureIndex].time % animation[textureIndex].frames;
	float normalVariation = normalVariations[faceNormal];
	float densityAdjustment = sqrt(dot(mvVertexPos, mvVertexPos))/abs(mvVertexPos.z);
	float fogDistance = calculateFogDistance(texelFetch(depthTexture, ivec2(gl_FragCoord.xy), 0).r, textureData[blockType].fogDensity*densityAdjustment);
	float airFogDistance = calculateFogDistance(texelFetch(depthTexture, ivec2(gl_FragCoord.xy), 0).r, fog.density*densityAdjustment);
	vec3 fogColor = unpackColor(textureData[blockType].fogColor);
	if(isBackFace == 0) {

		vec4 textureColor = texture(texture_sampler, vec3(getTextureCoordsNormal(startPosition/16, faceNormal), textureIndex))*vec4(ambientLight*normalVariation, 1);

		if (textureColor.a == 1) discard;

		textureColor.rgb *= textureColor.a;
		blendColor.rgb = unpackColor(textureData[blockType].absorption);

		// Fake reflection:
		// TODO: Also allow this for opaque pixels.
		// TODO: Change this when it rains.
		// TODO: Normal mapping.
		// TODO: Allow textures to contribute to this term.
		textureColor.rgb += (textureData[blockType].reflectivity*fixedCubeMapLookup(reflect(direction, normals[faceNormal])).xyz)*ambientLight*normalVariation;
		textureColor.rgb += texture(emissionSampler, vec3(getTextureCoordsNormal(startPosition/16, faceNormal), textureIndex)).rgb;
		blendColor.rgb *= 1 - textureColor.a;
		textureColor.a = 1;

		if(textureData[blockType].fogDensity == 0.0) {
			// Apply the air fog, compensating for the missing back-face:
			applyFrontfaceFog(airFogDistance, fog.color);
		} else {
			// Apply the block fog:
			applyFrontfaceFog(fogDistance, fogColor);
		}

		// Apply the texture+absorption
		fragColor.rgb *= blendColor.rgb;
		fragColor.rgb += textureColor.rgb*fragColor.a;

		// Apply the air fog:
		applyBackfaceFog(airFogDistance, fog.color);
	} else {
		// Apply the air fog:
		applyFrontfaceFog(airFogDistance, fog.color);

		// Apply the texture:
		vec4 textureColor = texture(texture_sampler, vec3(getTextureCoordsNormal(startPosition/16, faceNormal), textureIndex))*vec4(ambientLight*normalVariation, 1);
		blendColor.rgb = vec3(1 - textureColor.a);
		fragColor.rgb *= blendColor.rgb;
		fragColor.rgb += textureColor.rgb*textureColor.a*fragColor.a;

		// Apply the block fog:
		applyBackfaceFog(fogDistance, fogColor);
	}
}
