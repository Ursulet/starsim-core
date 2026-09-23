# Built-in presets

This directory contains 28 versioned Beginner recipes: Natural, Balanced, Detailed and Strong profiles for Jupiter, Saturn, Mars, Venus, Moon, Solar and Generic data.

`macros.waveletGains` stores the six StarSim Classic profile contributions in L1-to-L6 order. The Beginner mapper gives the nominal L1 contribution ten additional gain points and scales the remaining contributions proportionally above neutral, so the target-specific shape is preserved without adding ten points indiscriminately to broad layers. The profiles do not copy RegiStax values and do not alter Wavelet Gaussian width, Denoise or Threshold.

Preset selection stages the visible values; **Apply Preset** commits the full recipe. After application, each Beginner slider owns only its documented processor fields.
