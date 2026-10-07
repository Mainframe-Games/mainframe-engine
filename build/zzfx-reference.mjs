// Reference vectors for the ZzFX port (Tests/MainframeEngine.Tests/Audio/ZzfxTests.cs).
//
// Runs ZzFX 1.4.0's ZZFX.buildSamples, vendored verbatim below from
// https://github.com/KilledByAPixel/ZzFX/blob/751f17139d689b1320f0c4173031b038959e4bf8/ZzFX.js, over a set of
// parameter lists (randomness 0) and writes Tests/Content/Audio/zzfx-reference.json: per case the sample count and
// every 64th sample (before ZzFX's master volume). Run by hand when the cases change: node build/zzfx-reference.mjs
//
/*
  ZzFX MIT License
  
  Copyright (c) 2019 - Frank Force
  
  Permission is hereby granted, free of charge, to any person obtaining a copy
  of this software and associated documentation files (the "Software"), to deal
  in the Software without restriction, including without limitation the rights
  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
  copies of the Software, and to permit persons to whom the Software is
  furnished to do so, subject to the following conditions:
  
  The above copyright notice and this permission notice shall be included in all
  copies or substantial portions of the Software.
  
  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
  SOFTWARE.
  
*/

import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

Math.random = () => 0.5; // multiplied by randomness, which is 0 in every case

const ZZFX = {
    sampleRate: 44100,
    buildSamples: function
    (
        volume = 1, 
        randomness = .05,
        frequency = 220,
        attack = 0,
        sustain = 0,
        release = .1,
        shape = 0,
        shapeCurve = 1,
        slide = 0, 
        deltaSlide = 0, 
        pitchJump = 0, 
        pitchJumpTime = 0, 
        repeatTime = 0, 
        noise = 0,
        modulation = 0,
        bitCrush = 0,
        delay = 0,
        sustainVolume = 1,
        decay = 0,
        tremolo = 0,
        filter = 0
    )
    {
        // init parameters
        let sampleRate = this.sampleRate,
            PI2 = Math.PI*2, 
            abs = Math.abs, 
            sign = v => v<0?-1:1, 
            startSlide = slide *= 500 * PI2 / sampleRate / sampleRate,
            startFrequency = frequency *= 
                (1 + randomness*2*Math.random() - randomness) * PI2 / sampleRate,
            modOffset = 0, // modulation offset  
            repeat = 0,    // repeat offset
            crush = 0,     // bit crush offset
            jump = 1,      // pitch jump timer
            length,        // sample length
            b,             // sample buffer
            t = 0,         // sample time
            i = 0,         // sample index 
            s = 0,         // sample value
        f,             // wave frequency

        // biquad LP/HP filter
        quality = 2, w = PI2 * abs(filter) * 2 / sampleRate,
        cos = Math.cos(w), alpha = Math.sin(w) / 2 / quality,
        a0 = 1 + alpha, a1 = -2*cos / a0, a2 = (1 - alpha) / a0,
        b0 = (1 + sign(filter) * cos) / 2 / a0, 
        b1 = -(sign(filter) + cos) / a0, b2 = b0,
        x2 = 0, x1 = 0, y2 = 0, y1 = 0;

        // scale by sample rate
        const minAttack = 9; // prevent pop if attack is 0
        attack = attack * sampleRate || minAttack;
        decay *= sampleRate;
        sustain *= sampleRate;
        release *= sampleRate;
        delay *= sampleRate;
        deltaSlide *= 500 * PI2 / sampleRate**3;
        modulation *= PI2 / sampleRate;
        pitchJump *= PI2 / sampleRate;
        pitchJumpTime *= sampleRate;
        repeatTime = repeatTime * sampleRate | 0;

        // allocate the full sample buffer up front, much faster than growing an array
        length = attack + decay + sustain + release + delay | 0;
        b = new Float32Array(length > 0 ? length : 0);

        // generate waveform
        for(; i < length; b[i++] = s * volume)                 // sample
        {
            if (!(++crush%(bitCrush*100|0)))                   // bit crush
            {
                s = shape? shape>1? shape>2? shape>3? shape>4? // wave shape
                    (t/PI2%1 < shapeCurve/2? 1 : -1) :         // 5 square duty
                    Math.sin(t**3) :                           // 4 noise
                    Math.max(Math.min(Math.tan(t),1),-1):      // 3 tan
                    1-(2*t/PI2%2+2)%2:                         // 2 saw
                    1-4*abs(Math.round(t/PI2)-t/PI2):          // 1 triangle
                    Math.sin(t);                               // 0 sin

                s = (repeatTime ?
                        1 - tremolo + tremolo*Math.sin(PI2*i/repeatTime) // tremolo
                        : 1) *
                    (shape>4?s:sign(s)*abs(s)**shapeCurve) * // shape curve
                    (i < attack ? i/attack :                 // attack
                    i < attack + decay ?                     // decay
                    1-((i-attack)/decay)*(1-sustainVolume) : // decay falloff
                    i < attack  + decay + sustain ?          // sustain
                    sustainVolume :                          // sustain volume
                    i < length - delay ?                     // release
                    (length - i - delay)/release *           // release falloff
                    sustainVolume :                          // release volume
                    0);                                      // post release

                s = delay ? s/2 + (delay > i ? 0 :           // delay
                    (i<length-delay? 1 : (length-i)/delay) * // release delay 
                    b[i-delay|0]/2/volume) : s;              // sample delay

                if (filter)                                  // apply filter
                    s = y1 = b2*x2 + b1*(x2=x1) + b0*(x1=s) - a2*y2 - a1*(y2=y1);
            }

            f = (frequency += slide += deltaSlide) *// frequency
                Math.cos(modulation*modOffset++);   // modulation
            t += f + f*noise*(i*i*PI2%2-1);         // noise

            if (jump && ++jump > pitchJumpTime)     // pitch jump
            { 
                frequency += pitchJump;             // apply pitch jump
                startFrequency += pitchJump;        // also apply to start
                jump = 0;                           // stop pitch jump time
            } 

            if (repeatTime && !(++repeat % repeatTime)) // repeat
            { 
                frequency = startFrequency;   // reset frequency
                slide = startSlide;           // reset slide
                jump ||= 1;                   // reset pitch jump time
            }
        }

        return b; // return sample buffer
    }
};

// Randomness (index 1) is always 0: the port generates without it (it is applied per play).
const cases = {
    defaults: [, 0],
    sineEnvelope: [1, 0, 440, .01, .1, .2, 0],
    triangleCurve: [1, 0, 300, .02, .05, .1, 1, 1.5],
    sawSlide: [1, 0, 200, 0, .1, .2, 2, 1, 5, 1],
    tanShape: [1, 0, 150, .01, .05, .1, 3, 2],
    noiseShape: [1, 0, 100, 0, .05, .05, 4, 1],
    squareDuty: [1, 0, 500, 0, .05, .1, 5, .6],
    lowPass: [1, 0, 220, 0, .1, .1, 2, 1, , , , , , , , , , , , , 1000],
    highPass: [1, 0, 220, 0, .1, .1, 2, 1, , , , , , , , , , , , , -800],
    bitCrushNoise: [1, 0, 400, 0, .1, .1, 0, 1, , , , , , .5, , .3],
    delay: [.8, 0, 600, 0, .05, .1, 1, 1, , , , , , , , , .1],
    repeatTremolo: [1, 0, 300, 0, .2, .1, 0, 1, 2, , , , .05, , , , , , , .5],
    pitchJump: [1, 0, 800, 0, .1, .2, 0, 1, , , 300, .05],
    modulationDecay: [1, 0, 500, .02, .1, .2, 0, 1, , , , , , , 30, , , .5, .1],
    coin: [, 0, 925, .04, .3, .6, 1, .3, , 6.27, -184, .09, .17],
    explosion: [, 0, 333, .01, 0, .9, 4, 1.9, , , , , , .5, , .6],
};

const stride = 64;
const out = { zzfx: '1.4.0', commit: '751f17139d689b1320f0c4173031b038959e4bf8', stride, cases: [] };
for (const [name, params] of Object.entries(cases)) {
    const full = Array.from({ length: 21 }, (_, i) => params[i] ?? null);
    const samples = ZZFX.buildSamples(...full.map(v => v ?? undefined));
    const picked = [];
    for (let i = 0; i < samples.length; i += stride)
        picked.push(samples[i]);
    out.cases.push({ name, params: full, count: samples.length, samples: picked });
}

const target = join(dirname(fileURLToPath(import.meta.url)), '..', 'Tests', 'Content', 'Audio', 'zzfx-reference.json');
writeFileSync(target, JSON.stringify(out) + '\n');
console.log(`wrote ${out.cases.length} cases to ${target}`);
