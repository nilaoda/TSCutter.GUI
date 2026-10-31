// 色彩处理与 CPU 映射器共用查找表；IPT/感知色域处理参考 libplacebo（LGPL-2.1-or-later）。
using System;
using SkiaSharp;

namespace TSCutter.GUI.Rendering.Gpu;

internal static class VideoColorShader
{
    private static readonly Lazy<SKRuntimeEffect> EffectCache = new(() =>
    {
        var effect = SKRuntimeEffect.Create(Source, out var errors);
        return effect ?? throw new NotSupportedException("Video shader compilation failed: " + errors);
    });
    internal static SKRuntimeEffect Effect => EffectCache.Value;

    private const string Source = """
        uniform shader planeY;
        uniform shader planeU;
        uniform shader planeV;
        uniform shader transferTable;
        uniform shader toneTable;
        uniform shader gamutTable;
        uniform shader transferFine;
        uniform shader toneFine;
        uniform shader gamutFine;
        uniform float3 sampleScale;
        uniform float3 sampleAlpha;
        uniform float2 subsampling;
        uniform float2 chromaOffset;
        uniform float interleaved;
        uniform float validation;
        uniform float hdr;
        uniform float hlg;
        uniform float reshape;
        uniform float perceptual;
        uniform float3 signalOffset;
        uniform float3 nonlinear0; uniform float3 nonlinear1; uniform float3 nonlinear2;
        uniform float3 linear0; uniform float3 linear1; uniform float3 linear2;
        uniform float3 fromLms0; uniform float3 fromLms1; uniform float3 fromLms2;
        uniform float curveCounts[3];
        uniform float pivots[27];
        uniform float methods[24];
        uniform float orders[24];
        uniform float constants[24];
        uniform float coefficients[504];

        float3 matrix(float3 v, float3 a, float3 b, float3 c) {
            return float3(dot(a, v), dot(b, v), dot(c, v));
        }
        float3 rgbToLms(float3 v) {
            return matrix(v, float3(0.29576408, 0.62307245, 0.08116675),
                float3(0.15619198, 0.72725164, 0.11655793), float3(0.03510228, 0.15658995, 0.80830303));
        }
        float3 lmsToRgb(float3 v) {
            return matrix(v, float3(6.17353266, -5.32089882, 0.14735489),
                float3(-1.32403191, 2.56026977, -0.23623862), float3(-0.01159839, -0.26492145, 1.27652634));
        }
        float3 lmsToIpt(float3 v) {
            return matrix(v, float3(0.4, 0.4, 0.2), float3(4.455, -4.851, 0.396), float3(0.8056, 0.3572, -1.1628));
        }
        float3 iptToLms(float3 v) {
            return matrix(v, float3(1, 0.09756893, 0.20522643), float3(1, -0.11387649, 0.13321716),
                float3(1, 0.03261511, -0.67688718));
        }
        float2 tablePosition(float index) {
            float row = floor(index / 256.0);
            return float2(index - row * 256.0 + 0.5, row + 0.5);
        }
        float3 transfer(float value, half4 _input) {
            float pos = clamp(value, 0, 1) * 16384.0;
            float index = min(floor(pos), 16383.0);
            return mix((sample(transferTable, tablePosition(index)).rgb + (sample(transferFine, tablePosition(index)).rgb - 0.5) / 2048.0),
                (sample(transferTable, tablePosition(index + 1)).rgb + (sample(transferFine, tablePosition(index + 1)).rgb - 0.5) / 2048.0), pos - index);
        }
        float3 tone(float value, half4 _input) {
            float pos = clamp(value, 0, 1) * 16384.0;
            float index = min(floor(pos), 16383.0);
            return mix((sample(toneTable, tablePosition(index)).rgb + (sample(toneFine, tablePosition(index)).rgb - 0.5) / 2048.0),
                (sample(toneTable, tablePosition(index + 1)).rgb + (sample(toneFine, tablePosition(index + 1)).rgb - 0.5) / 2048.0), pos - index);
        }
        float3 decodePq(float3 v) {
            return 10000.0 * float3(transfer(v.x, half4(1)).r, transfer(v.y, half4(1)).r, transfer(v.z, half4(1)).r);
        }
        float3 encodePq(float3 v) {
            v = sqrt(max(v, float3(0))) * 0.01;
            return float3(transfer(v.x, half4(1)).g, transfer(v.y, half4(1)).g, transfer(v.z, half4(1)).g);
        }
        float3 decodeMapped(float3 v) {
            return float3(v.x <= 0.000001 ? 0 : transfer(v.x, half4(1)).r * (10000.0 / 203.0),
                v.y <= 0.000001 ? 0 : transfer(v.y, half4(1)).r * (10000.0 / 203.0),
                v.z <= 0.000001 ? 0 : transfer(v.z, half4(1)).r * (10000.0 / 203.0));
        }
        float hull(float v) { return ((v - 6) * v + 9) * v; }
        float softClip(float v, float peak) {
            float knee = 0.7;
            if (peak <= 1 || v <= knee) return clamp(v, 0, 1);
            float a = -knee * knee * (peak - 1) / (knee * knee - 2 * knee + peak);
            float b = (knee * knee - 2 * knee * peak + peak) / max(0.000001, peak - 1);
            float scale = (b + knee) * (b + knee) / (b - a);
            return clamp(scale * (min(v, peak) + a) / (min(v, peak) + b), 0, 1);
        }
        float reshapeSignal(float curveNumber, float3 signal) {
            int curve = int(curveNumber);
            signal = clamp(signal, 0, 1);
            float v = signal[curve];
            int piece = 0;
            for (int j = 1; j < 8; ++j) {
                if (j >= int(curveCounts[curve]) || v < pivots[curve * 9 + j]) break;
                piece = j;
            }
            int slot = curve * 8 + piece;
            int index = slot * 21;
            float result = (coefficients[index + 2] * v + coefficients[index + 1]) * v + coefficients[index];
            if (methods[slot] > 0.5) {
                result = constants[slot];
                float3 power = signal;
                float3 cross = float3(signal.x * signal.y, signal.x * signal.z, signal.y * signal.z);
                float triple = signal.x * signal.y * signal.z;
                float3 crossPower = cross;
                float triplePower = triple;
                for (int order = 0; order < 3; ++order) {
                    if (order >= int(orders[slot])) break;
                    int k = index + order * 7;
                    result += dot(float3(coefficients[k], coefficients[k + 1], coefficients[k + 2]), power)
                        + dot(float3(coefficients[k + 3], coefficients[k + 4], coefficients[k + 5]), crossPower)
                        + coefficients[k + 6] * triplePower;
                    power *= signal; crossPower *= cross; triplePower *= triple;
                }
            }
            return clamp(result, pivots[curve * 9], pivots[curve * 9 + int(curveCounts[curve])]);
        }
        float4 readPlane(float2 p, float plane, half4 _input) {
            if (plane == 0) return sample(planeY, p);
            if (plane == 1) return sample(planeU, p);
            return sample(planeV, p);
        }
        float4 samplePlane(float2 p, float plane) {
            p -= 0.5;
            float2 base = floor(p);
            float2 weight = p - base;
            return mix(mix(readPlane(base + float2(0.5, 0.5), plane, half4(1)), readPlane(base + float2(1.5, 0.5), plane, half4(1)), weight.x),
                mix(readPlane(base + float2(0.5, 1.5), plane, half4(1)), readPlane(base + float2(1.5, 1.5), plane, half4(1)), weight.x), weight.y);
        }
        half4 main(float2 pos) {
            if (validation > 0.5) return half4(0.2, 0.8, 0.4, 1);
            float2 uvPos = (pos - 0.5 - chromaOffset) / subsampling + 0.5;
            float4 y = samplePlane(pos, 0);
            float4 u = samplePlane(uvPos, 1);
            float4 v = samplePlane(uvPos, 2);
            float3 signal = float3(sampleAlpha.x > 0.5 ? y.a : y.r,
                sampleAlpha.y > 0.5 ? u.a : u.r, sampleAlpha.z > 0.5 ? v.a : (interleaved > 0.5 ? v.g : v.r)) * sampleScale;
            if (reshape > 0.5) signal = float3(reshapeSignal(0, signal), reshapeSignal(1, signal), reshapeSignal(2, signal));
            float3 nonlinear = matrix(signal - signalOffset, nonlinear0, nonlinear1, nonlinear2);
            if (hdr < 0.5) return half4(clamp(nonlinear, 0, 1), 1);
            float3 linear = hlg > 0.5 ? float3(tone(nonlinear.r, half4(1)).b, tone(nonlinear.g, half4(1)).b, tone(nonlinear.b, half4(1)).b) : decodePq(nonlinear);
            float3 rgb = matrix(linear, linear0, linear1, linear2);
            float scale = hlg > 0.5 ? 1000.0 * tone(dot(rgb, float3(0.2126, 0.7152, 0.0722)), half4(1)).g : 1;
            float3 lms = encodePq(rgbToLms(rgb) * scale);
            float3 ipt = lmsToIpt(lms);
            float mapped = tone(ipt.x, half4(1)).r;
            float chroma = min(ipt.x / max(mapped, 0.000001), hull(mapped) / max(hull(ipt.x), 0.000001));
            if (abs(ipt.y) < 0.0001 && abs(ipt.z) < 0.0001) ipt.yz = float2(0);
            lms = iptToLms(float3(mapped, ipt.yz * chroma));
            float3 decoded = decodeMapped(lms);
            if (perceptual > 0.5) {
                ipt = lmsToIpt(lms);
                float magnitude = length(ipt.yz);
                float angle = abs(ipt.y) < 0.0000001 ? sign(ipt.z) * 1.57079632679 : atan(ipt.z / ipt.y);
                if (ipt.y < 0) angle += ipt.z >= 0 ? 3.14159265359 : -3.14159265359;
                float hue = (angle / 6.28318530718 + 0.5) * 360.0;
                float index = floor(hue);
                float2 p0 = float2((index - floor(index / 360.0) * 360.0) + 0.5, 0.5);
                float2 p1 = float2((index + 1 - floor((index + 1) / 360.0) * 360.0) + 0.5, 0.5);
                float2 boundaries = mix(sample(gamutTable, p0).rg + (sample(gamutFine, p0).rg - 0.5) / 2048.0,
                    sample(gamutTable, p1).rg + (sample(gamutFine, p1).rg - 0.5) / 2048.0, hue - index);
                float blend = clamp((magnitude / max(max(boundaries.x, boundaries.y), 0.000001) - 0.3) / 0.7, 0, 1);
                blend = blend * blend * (3 - 2 * blend) * 0.8;
                if (blend > 0) {
                    float3 relative = matrix(decoded, fromLms0, fromLms1, fromLms2);
                    float3 destination = lmsToIpt(encodePq(rgbToLms(relative) * 203.0));
                    lms = iptToLms(mix(ipt, destination, blend));
                    decoded = decodeMapped(lms);
                }
            }
            rgb = lmsToRgb(decoded);
            float peak = max(rgb.r, max(rgb.g, rgb.b));
            rgb = float3(softClip(rgb.r, peak), softClip(rgb.g, peak), softClip(rgb.b, peak));
            rgb = clamp((rgb - 0.001) / 0.999, 0, 1);
            rgb = float3(transfer(rgb.r, half4(1)).b, transfer(rgb.g, half4(1)).b, transfer(rgb.b, half4(1)).b);
            return half4(rgb, 1);
        }
        """;
}
