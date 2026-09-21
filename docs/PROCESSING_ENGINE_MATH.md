# StarSim Core Production Processing Mathematics

Last updated: 2026-09-21

This document defines the production algorithms implemented by the existing native engine. All stages consume and produce planar `float32`, use reflected boundaries, preserve finite values, and avoid `[0,1]` clipping except where an operator explicitly requires a non-negative domain (Richardson–Lucy) or a bounded local envelope (Deringing). Display and integer export are the quantization boundaries. A disabled stage is an exact input copy.

## Convolution contract

Gaussian filters use `G(x)=exp(-x²/(2σ²))`, radius `ceil(3σ)` capped at 64 samples, separately normalized horizontal/vertical passes, and reflection at both axes. The à-trous backend uses the normalized B3 kernel `[1,4,6,4,1]/16` with reflected sampling.

## Production stage order

`Source → RGB Align → Noise Reduction → Richardson–Lucy → Wavelet → Unsharp → Multi-scale Sharpen → Deringing → Local Detail → Advanced Color → RGB Balance → Saturation → Advanced Tone → Exposure → Contrast → Gamma → Viewer/Export`

No stage writes parameters owned by another stage.

## Wavelet

The existing `core.wavelet` processor has two analysis backends and one common reconstruction/control model.

Recursive Gaussian:

`L0=Input`, `Li=Gaussian(L(i-1),σi)`, `Wi=L(i-1)-Li`.

Linear widths use `σi=σinitial+(i-1)σstep`; Dyadic widths use `σi=σinitial·2^(i-1)`. A positive per-layer override replaces the derived width. The calibrated Gaussian default is Linear with `σinitial=0.25` and `σstep=0.05`; Dyadic remains an explicit broad-scale choice.

À-trous B3-spline:

`C0=Input`, `Ci=AtrousB3(C(i-1),2^(i-1))`, `Wi=C(i-1)-Ci`.

Both reconstruct as `Low6 + globalStrength·Σ(gaini·processed(Wi))`. Gain 1 is neutral, 0 removes the band, and values above 1 enhance only that band. Classic displays and writes this gain directly (`52.7` means factor `52.7`), rather than applying the former `/100 + 1` attenuation. Denoise first applies `sign(w)·max(|w|-D,0)`; Threshold then suppresses the remaining coefficient when `|w|<T`. Classic writes only its corresponding gain. Linked mode reuses Layer 1 settings only when explicitly enabled.

## Sharpening and local detail

- Unsharp: `B=Gaussian(Input,σ)`, `D=sign(Input-B)·max(|Input-B|-T,0)`, `Output=Input+Amount·D`.
- Multi-scale: `B1=Gaussian(Input,σfine)`, `B2=Gaussian(B1,σbroad)`, `D1=Input-B1`, `D2=B1-B2`, `Output=Input+Afine·soft(D1,T)+Abroad·soft(D2,T)`.
- Local Detail: independent local and micro Gaussian differences are added with their own amounts. The existing edge-protection gate reduces amplification at strong local transitions.

## Noise reduction

Noise Reduction performs a three-band recursive Gaussian decomposition. Noise sigma is estimated from the finest signed detail by `median(abs(W1-median(W1)))/0.67448975`. Each band uses soft shrinkage with `T=Strength·ThresholdScale·sigmaNoise`. Fine Detail Protection continuously reduces shrinkage for coefficients well above the estimated noise. RGB is separated into Rec.709 luminance and three signed `channel-luminance` components; luminance and chrominance strengths are independent. A zero strength is exact identity.

## Richardson–Lucy and Deringing

Richardson–Lucy uses a normalized symmetric Gaussian PSF and `epsilon=1e-6`:

`blur=h*x`, `ratio=max(y,0)/(blur+epsilon)`, `candidate=max(0,x*(h*ratio))`, `x←lerp(x,candidate,1-damping)`.

The final output is `Input+Strength·(RL-Input)`. Damping 1 suppresses all updates; Strength 0 is exact identity. Deringing is not embedded in RL.

Deringing currently uses the documented self-reference fallback: local min/max are measured from neighboring samples of its own input, `extension=EdgeProtection·(max-min)`, `corrected=clamp(P,min-extension,max+extension)`, and `Output=lerp(P,corrected,Strength)`. It has no hidden access to an upstream buffer.

## RGB alignment

Green is the reference. Automatic Red/Blue translations use zero-padded 2-D FFT phase correlation with normalized cross-power, a bounded peak search, and parabolic subpixel refinement. Exact integer peaks are stabilized against FFT roundoff. Automatic and manual offsets share normalized separable Lanczos-3 resampling, `sinc(x)·sinc(x/3)` for `|x|<3`, with reflection and no wraparound.

## Color and tone

RGB Balance multiplies each channel independently. Saturation scales channel distance from Rec.709 luminance. Advanced Color owns only white gains, temperature, tint, and adaptive vibrance.

Exposure is `x·2^EV`; Contrast is `0.5+factor·(x-0.5)`; Gamma is signed: `sign(x)·|x|^(1/gamma)`. Advanced Tone owns brightness, black/white normalization, highlights, and shadows. These controls never write each other's state.

## Numerical invariants

Neutral processor states reconstruct within float32 tolerance. Native validation rejects non-finite parameters and payload mismatches. Validation covers both wavelet backends, signed gamma, RL damping, RGB registration, neutral identity, cache boundaries, deterministic output, finite output, and source immutability.
