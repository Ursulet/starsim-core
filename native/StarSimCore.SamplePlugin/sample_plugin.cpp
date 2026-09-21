#include "starsim_core_plugin.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace
{
constexpr SSC_PluginParameterDef kInvertTintParameters[] = {
    {
        sizeof(SSC_PluginParameterDef),
        "invert",
        "Invert Amount",
        0.0,
        0.0,
        1.0,
        0.05
    },
    {
        sizeof(SSC_PluginParameterDef),
        "red_tint",
        "Red Tint Multiplier",
        1.0,
        0.0,
        2.0,
        0.05
    },
    {
        sizeof(SSC_PluginParameterDef),
        "green_tint",
        "Green Tint Multiplier",
        1.0,
        0.0,
        2.0,
        0.05
    },
    {
        sizeof(SSC_PluginParameterDef),
        "blue_tint",
        "Blue Tint Multiplier",
        1.0,
        0.0,
        2.0,
        0.05
    }
};

constexpr SSC_PluginParameterDef kGrayscaleParameters[] = {
    {
        sizeof(SSC_PluginParameterDef),
        "strength",
        "B&W Strength",
        1.0,
        0.0,
        1.0,
        0.01
    }
};

constexpr SSC_PluginProcessorDef kProcessors[] = {
    {
        sizeof(SSC_PluginProcessorDef),
        "diagnostic.invert_tint",
        "Diagnostic Invert and Tint",
        "Diagnostic",
        SSC_PLUGIN_CAP_GRAYSCALE | SSC_PLUGIN_CAP_RGB | SSC_PLUGIN_CAP_SUPPORTS_PREVIEW,
        static_cast<uint32_t>(sizeof(kInvertTintParameters) / sizeof(kInvertTintParameters[0])),
        kInvertTintParameters
    },
    {
        sizeof(SSC_PluginProcessorDef),
        "diagnostic.rgb_to_bw",
        "RGB to B&W Mix",
        "Diagnostic",
        SSC_PLUGIN_CAP_GRAYSCALE | SSC_PLUGIN_CAP_RGB | SSC_PLUGIN_CAP_SUPPORTS_PREVIEW,
        static_cast<uint32_t>(sizeof(kGrayscaleParameters) / sizeof(kGrayscaleParameters[0])),
        kGrayscaleParameters
    }
};

constexpr SSC_PluginInfo kPluginInfo = {
    sizeof(SSC_PluginInfo),
    STARSIM_CORE_PLUGIN_ABI_VERSION,
    "org.starsim.plugin.diagnostic",
    "StarSim Core Diagnostic Sample Plugin",
    "1.1.0",
    "StarSim Core Team",
    "Sample plugin demonstrating generated controls with Invert/Tint and RGB-to-B&W processors.",
    static_cast<uint32_t>(sizeof(kProcessors) / sizeof(kProcessors[0]))
};
}

extern "C" {

STARSIM_PLUGIN_API int32_t ssc_plugin_get_info(SSC_PluginInfo* out_info)
{
    if (!out_info || out_info->struct_size < sizeof(SSC_PluginInfo))
    {
        return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
    }
    *out_info = kPluginInfo;
    return SSC_PLUGIN_STATUS_OK;
}

STARSIM_PLUGIN_API int32_t ssc_plugin_get_processor_def(uint32_t index, SSC_PluginProcessorDef* out_def)
{
    if (index >= kPluginInfo.processor_count || !out_def || out_def->struct_size < sizeof(SSC_PluginProcessorDef))
    {
        return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
    }
    *out_def = kProcessors[index];
    return SSC_PLUGIN_STATUS_OK;
}

STARSIM_PLUGIN_API int32_t ssc_plugin_process(
    uint32_t processor_index,
    const SSC_PluginImageBuffer* in_buffer,
    SSC_PluginImageBuffer* out_buffer,
    const double* parameters,
    uint32_t parameter_count,
    SSC_PluginProcessingQuality quality,
    const SSC_PluginHostCallbacks* callbacks)
{
    if (processor_index >= kPluginInfo.processor_count || !in_buffer || !out_buffer || !parameters)
    {
        return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
    }
    if (in_buffer->struct_size < sizeof(SSC_PluginImageBuffer) ||
        out_buffer->struct_size < sizeof(SSC_PluginImageBuffer) ||
        in_buffer->width == 0 ||
        in_buffer->height == 0 ||
        (in_buffer->channel_count != 1 && in_buffer->channel_count != 3) ||
        in_buffer->width != out_buffer->width ||
        in_buffer->height != out_buffer->height ||
        in_buffer->channel_count != out_buffer->channel_count ||
        !in_buffer->planes || !out_buffer->planes)
    {
        return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
    }
    const SSC_PluginProcessorDef& processor = kProcessors[processor_index];
    if (parameter_count != processor.parameter_count ||
        (quality != SSC_PLUGIN_QUALITY_INTERACTIVE_PREVIEW &&
         quality != SSC_PLUGIN_QUALITY_FULL_RESOLUTION))
    {
        return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
    }
    if (callbacks && callbacks->struct_size < sizeof(SSC_PluginHostCallbacks))
    {
        return SSC_PLUGIN_STATUS_ABI_MISMATCH;
    }

    for (uint32_t p = 0; p < parameter_count; ++p)
    {
        if (!std::isfinite(parameters[p]) ||
            parameters[p] < processor.parameters[p].minimum_value ||
            parameters[p] > processor.parameters[p].maximum_value)
        {
            return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
        }
    }

    if (in_buffer->height > std::numeric_limits<size_t>::max() / in_buffer->width)
    {
        return SSC_PLUGIN_STATUS_OUT_OF_MEMORY;
    }

    const size_t pixel_count = static_cast<size_t>(in_buffer->width) * in_buffer->height;
    const uint32_t channels = in_buffer->channel_count;

    if (processor_index == 1)
    {
        const float strength = static_cast<float>(parameters[0]);
        for (uint32_t c = 0; c < channels; ++c)
        {
            if (!in_buffer->planes[c] || !out_buffer->planes[c])
            {
                return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
            }
        }

        for (size_t i = 0; i < pixel_count; ++i)
        {
            if ((i & 0xFFFFu) == 0 && callbacks && callbacks->is_cancelled &&
                callbacks->is_cancelled(callbacks->user_data))
            {
                return SSC_PLUGIN_STATUS_CANCELLED;
            }

            if (channels == 1)
            {
                out_buffer->planes[0][i] = in_buffer->planes[0][i];
                continue;
            }

            const float red = in_buffer->planes[0][i];
            const float green = in_buffer->planes[1][i];
            const float blue = in_buffer->planes[2][i];
            const float luminance = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
            out_buffer->planes[0][i] = std::clamp(red + strength * (luminance - red), 0.0f, 1.0f);
            out_buffer->planes[1][i] = std::clamp(green + strength * (luminance - green), 0.0f, 1.0f);
            out_buffer->planes[2][i] = std::clamp(blue + strength * (luminance - blue), 0.0f, 1.0f);
        }

        if (callbacks && callbacks->report_progress)
        {
            callbacks->report_progress(callbacks->user_data, 1.0, "Mixing RGB channels to B&W luminance");
        }
        if (callbacks && callbacks->log_message)
        {
            callbacks->log_message(callbacks->user_data, 0, "Diagnostic RGB-to-B&W processing completed.");
        }
        return SSC_PLUGIN_STATUS_OK;
    }

    const float invert = static_cast<float>(parameters[0]);
    const float r_mult = static_cast<float>(parameters[1]);
    const float g_mult = static_cast<float>(parameters[2]);
    const float b_mult = static_cast<float>(parameters[3]);

    for (uint32_t c = 0; c < channels; ++c)
    {
        const float* src = in_buffer->planes[c];
        float* dst = out_buffer->planes[c];
        if (!src || !dst)
        {
            return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;
        }

        const float mult = (channels == 1) ? r_mult : (c == 0 ? r_mult : (c == 1 ? g_mult : b_mult));

        if (callbacks && callbacks->is_cancelled && callbacks->is_cancelled(callbacks->user_data))
        {
            return SSC_PLUGIN_STATUS_CANCELLED;
        }

        for (size_t i = 0; i < pixel_count; ++i)
        {
            if ((i & 0xFFFFu) == 0 && callbacks && callbacks->is_cancelled &&
                callbacks->is_cancelled(callbacks->user_data))
            {
                return SSC_PLUGIN_STATUS_CANCELLED;
            }
            float val = src[i];
            if (invert > 0.0f)
            {
                val = val + invert * (1.0f - val - val); // linear interpolation: (1 - invert) * val + invert * (1 - val)
            }
            val *= mult;
            dst[i] = std::clamp(val, 0.0f, 1.0f);
        }

        if (callbacks && callbacks->report_progress)
        {
            const double fraction = static_cast<double>(c + 1) / static_cast<double>(channels);
            callbacks->report_progress(callbacks->user_data, fraction, "Applying diagnostic invert and tint");
        }
    }

    if (callbacks && callbacks->log_message)
    {
        callbacks->log_message(callbacks->user_data, 0, "Diagnostic invert/tint processing completed.");
    }

    return SSC_PLUGIN_STATUS_OK;
}

}
