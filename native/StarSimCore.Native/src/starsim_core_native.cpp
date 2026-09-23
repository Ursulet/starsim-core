#include "starsim_core_native.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <complex>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>

#include <png.h>
#include <tiffio.h>

#if defined(_WIN32)
#define NOMINMAX
#include <windows.h>
#endif

namespace
{
thread_local std::string last_error;
std::atomic<uint64_t> next_identity{1};

class FloatBufferPool
{
public:
    class Lease
    {
    public:
        Lease(FloatBufferPool& owner, std::vector<float>&& buffer) noexcept
            : owner_(&owner), buffer_(std::move(buffer)) {}
        Lease(const Lease&) = delete;
        Lease& operator=(const Lease&) = delete;
        Lease(Lease&& other) noexcept
            : owner_(std::exchange(other.owner_, nullptr)), buffer_(std::move(other.buffer_)) {}
        ~Lease() { if (owner_ != nullptr) owner_->release(std::move(buffer_)); }
        std::vector<float>& values() noexcept { return buffer_; }
    private:
        FloatBufferPool* owner_;
        std::vector<float> buffer_;
    };

    Lease acquire(size_t count)
    {
        std::lock_guard lock(gate_);
        auto best = buffers_.end();
        for (auto candidate = buffers_.begin(); candidate != buffers_.end(); ++candidate)
        {
            if (candidate->capacity() >= count &&
                (best == buffers_.end() || candidate->capacity() < best->capacity()))
                best = candidate;
        }
        std::vector<float> result;
        if (best != buffers_.end())
        {
            retained_bytes_ -= best->capacity() * sizeof(float);
            result = std::move(*best);
            buffers_.erase(best);
            ++reuse_count_;
        }
        else
        {
            ++allocation_count_;
        }
        result.resize(count);
        return Lease(*this, std::move(result));
    }

    void configure(uint64_t maximum_retained_bytes) noexcept
    {
        std::lock_guard lock(gate_);
        maximum_retained_bytes_ = maximum_retained_bytes;
        trim_to_limit();
    }

    void trim() noexcept
    {
        std::lock_guard lock(gate_);
        buffers_.clear();
        retained_bytes_ = 0;
    }

    void metrics(
        uint64_t& bytes,
        uint32_t& count,
        uint64_t& reuse_count,
        uint64_t& allocation_count) noexcept
    {
        std::lock_guard lock(gate_);
        bytes = retained_bytes_;
        count = static_cast<uint32_t>(std::min<size_t>(buffers_.size(), UINT32_MAX));
        reuse_count = reuse_count_;
        allocation_count = allocation_count_;
    }

private:
    void release(std::vector<float>&& buffer) noexcept
    {
        try
        {
            std::lock_guard lock(gate_);
            buffer.clear();
            const uint64_t bytes = buffer.capacity() * sizeof(float);
            if (bytes == 0 || bytes > maximum_retained_bytes_ ||
                retained_bytes_ > maximum_retained_bytes_ - bytes)
                return;
            retained_bytes_ += bytes;
            buffers_.push_back(std::move(buffer));
        }
        catch (...) {}
    }

    void trim_to_limit() noexcept
    {
        while (retained_bytes_ > maximum_retained_bytes_ && !buffers_.empty())
        {
            retained_bytes_ -= buffers_.back().capacity() * sizeof(float);
            buffers_.pop_back();
        }
    }

    std::mutex gate_;
    std::vector<std::vector<float>> buffers_;
    uint64_t retained_bytes_{};
    uint64_t maximum_retained_bytes_{UINT64_C(256) * 1024 * 1024};
    uint64_t reuse_count_{};
    uint64_t allocation_count_{};
};

FloatBufferPool float_buffer_pool;

struct ImageStorage
{
    SSC_ImageDescriptor descriptor{};
    std::vector<float> planar_pixels;
    uint64_t source_identity{};
};

void clear_error() noexcept
{
    last_error.clear();
}

SSC_Status fail(SSC_Status status, const char* message) noexcept
{
    try
    {
        last_error = message == nullptr ? "Unknown native error." : message;
    }
    catch (...)
    {
        last_error.clear();
    }

    return status;
}

bool checked_value_count(
    const SSC_ImageDescriptor& descriptor,
    uint64_t& result) noexcept
{
    const uint64_t width = descriptor.width;
    const uint64_t height = descriptor.height;
    const uint64_t channels = descriptor.channel_count;

    if (width == 0 || height == 0 || channels == 0)
    {
        return false;
    }

    if (width > std::numeric_limits<uint64_t>::max() / height)
    {
        return false;
    }

    const uint64_t pixels = width * height;
    if (pixels > std::numeric_limits<uint64_t>::max() / channels)
    {
        return false;
    }

    result = pixels * channels;
    return result <= static_cast<uint64_t>(std::numeric_limits<size_t>::max());
}

SSC_Status validate_descriptor(
    const SSC_ImageDescriptor* descriptor,
    uint64_t supplied_value_count) noexcept
{
    if (descriptor == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image descriptor is null.");
    }

    if (descriptor->struct_size < sizeof(SSC_ImageDescriptor))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image descriptor struct_size is too small.");
    }

    if (descriptor->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Image descriptor ABI version is not supported.");
    }

    if (descriptor->channel_count != 1 && descriptor->channel_count != 3)
    {
        return fail(SSC_STATUS_UNSUPPORTED, "Only one-channel grayscale and three-channel RGB images are supported.");
    }

    if ((descriptor->channel_count == 1 && descriptor->color_model != SSC_COLOR_MODEL_GRAYSCALE) ||
        (descriptor->channel_count == 3 && descriptor->color_model != SSC_COLOR_MODEL_RGB))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Color model does not match channel count.");
    }

    if (descriptor->source_bit_depth != 8 && descriptor->source_bit_depth != 16)
    {
        return fail(SSC_STATUS_UNSUPPORTED, "Source bit depth must be 8 or 16.");
    }

    if (descriptor->working_pixel_format != SSC_PIXEL_FORMAT_FLOAT32_PLANAR)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Working pixel format must be float32 planar for ABI v1.");
    }

    uint64_t expected_value_count = 0;
    if (!checked_value_count(*descriptor, expected_value_count))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image dimensions overflow or are zero.");
    }

    if (expected_value_count != supplied_value_count)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Pixel value count does not match the descriptor.");
    }

    return SSC_STATUS_OK;
}

struct DecodedImage
{
    SSC_ImageDescriptor descriptor{};
    SSC_ImageFileFormat file_format{};
    bool had_alpha{};
    std::vector<float> interleaved_pixels;
};

#if defined(_WIN32)
bool utf8_to_wide(const char* value, std::wstring& result) noexcept
{
    if (value == nullptr || *value == '\0')
    {
        return false;
    }

    const int length = MultiByteToWideChar(
        CP_UTF8,
        MB_ERR_INVALID_CHARS,
        value,
        -1,
        nullptr,
        0);
    if (length <= 1)
    {
        return false;
    }

    try
    {
        result.resize(static_cast<size_t>(length));
    }
    catch (...)
    {
        return false;
    }

    if (MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            value,
            -1,
            result.data(),
            length) != length)
    {
        result.clear();
        return false;
    }

    result.resize(static_cast<size_t>(length - 1));
    return true;
}

FILE* open_read_only(const std::wstring& path) noexcept
{
    FILE* file = nullptr;
    return _wfopen_s(&file, path.c_str(), L"rb") == 0 ? file : nullptr;
}
#else
FILE* open_read_only(const std::string& path) noexcept
{
    return std::fopen(path.c_str(), "rb");
}
#endif

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4611)
#endif
SSC_Status decode_png(
#if defined(_WIN32)
    const std::wstring& path,
#else
    const std::string& path,
#endif
    DecodedImage& decoded) noexcept
{
    FILE* file = open_read_only(path);
    if (file == nullptr)
    {
        return fail(SSC_STATUS_IO_ERROR, "PNG could not be opened for read-only access.");
    }

    png_structp png = png_create_read_struct(PNG_LIBPNG_VER_STRING, nullptr, nullptr, nullptr);
    if (png == nullptr)
    {
        std::fclose(file);
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate the PNG decoder.");
    }

    png_infop info = png_create_info_struct(png);
    if (info == nullptr)
    {
        png_destroy_read_struct(&png, nullptr, nullptr);
        std::fclose(file);
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate PNG metadata.");
    }

    png_bytep raw_pixels = nullptr;
    png_bytep* rows = nullptr;
    if (setjmp(png_jmpbuf(png)) != 0)
    {
        std::free(rows);
        std::free(raw_pixels);
        png_destroy_read_struct(&png, &info, nullptr);
        std::fclose(file);
        return fail(SSC_STATUS_DECODE_ERROR, "PNG data is corrupt or unsupported.");
    }

    png_init_io(png, file);
    png_read_info(png, info);

    const png_uint_32 width = png_get_image_width(png, info);
    const png_uint_32 height = png_get_image_height(png, info);
    const int bit_depth = png_get_bit_depth(png, info);
    const int color_type = png_get_color_type(png, info);
    if (width == 0 || height == 0 || (bit_depth != 8 && bit_depth != 16))
    {
        png_error(png, "Only non-empty 8-bit and 16-bit PNG images are supported.");
    }

    uint32_t channel_count = 0;
    if (color_type == PNG_COLOR_TYPE_GRAY || color_type == PNG_COLOR_TYPE_GRAY_ALPHA)
    {
        channel_count = 1;
    }
    else if (color_type == PNG_COLOR_TYPE_RGB || color_type == PNG_COLOR_TYPE_RGB_ALPHA)
    {
        channel_count = 3;
    }
    else
    {
        png_error(png, "Only grayscale and RGB PNG images are supported.");
    }

    const bool had_alpha =
        color_type == PNG_COLOR_TYPE_GRAY_ALPHA || color_type == PNG_COLOR_TYPE_RGB_ALPHA;
    if (had_alpha)
    {
        png_set_strip_alpha(png);
    }
#if defined(_WIN32)
    if (bit_depth == 16)
    {
        png_set_swap(png);
    }
#endif
    png_read_update_info(png, info);

    const png_size_t row_bytes = png_get_rowbytes(png, info);
    if (row_bytes == 0 || row_bytes > SIZE_MAX / height)
    {
        png_error(png, "PNG dimensions overflow native storage.");
    }

    raw_pixels = static_cast<png_bytep>(std::malloc(row_bytes * height));
    rows = static_cast<png_bytep*>(std::malloc(sizeof(png_bytep) * height));
    if (raw_pixels == nullptr || rows == nullptr)
    {
        png_error(png, "Unable to allocate PNG pixel storage.");
    }
    for (png_uint_32 y = 0; y < height; ++y)
    {
        rows[y] = raw_pixels + static_cast<size_t>(y) * row_bytes;
    }

    png_read_image(png, rows);
    png_read_end(png, nullptr);
    png_destroy_read_struct(&png, &info, nullptr);
    std::fclose(file);
    std::free(rows);
    rows = nullptr;

    try
    {
        const size_t value_count =
            static_cast<size_t>(width) * height * channel_count;
        decoded.interleaved_pixels.resize(value_count);
        if (bit_depth == 8)
        {
            for (size_t index = 0; index < value_count; ++index)
            {
                decoded.interleaved_pixels[index] =
                    static_cast<float>(raw_pixels[index]) / 255.0f;
            }
        }
        else
        {
            for (size_t index = 0; index < value_count; ++index)
            {
                uint16_t value = 0;
                std::memcpy(&value, raw_pixels + index * sizeof(uint16_t), sizeof(value));
                decoded.interleaved_pixels[index] =
                    static_cast<float>(value) / 65535.0f;
            }
        }

        decoded.descriptor.struct_size = sizeof(SSC_ImageDescriptor);
        decoded.descriptor.abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
        decoded.descriptor.width = width;
        decoded.descriptor.height = height;
        decoded.descriptor.channel_count = channel_count;
        decoded.descriptor.source_bit_depth = static_cast<uint32_t>(bit_depth);
        decoded.descriptor.color_model = channel_count == 1
            ? SSC_COLOR_MODEL_GRAYSCALE
            : SSC_COLOR_MODEL_RGB;
        decoded.descriptor.working_pixel_format = SSC_PIXEL_FORMAT_FLOAT32_PLANAR;
        decoded.file_format = SSC_IMAGE_FILE_FORMAT_PNG;
        decoded.had_alpha = had_alpha;
    }
    catch (const std::bad_alloc&)
    {
        std::free(raw_pixels);
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate normalized PNG pixels.");
    }

    std::free(raw_pixels);
    return SSC_STATUS_OK;
}
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

SSC_Status decode_tiff(
#if defined(_WIN32)
    const std::wstring& path,
#else
    const std::string& path,
#endif
    DecodedImage& decoded) noexcept
{
#if defined(_WIN32)
    TIFF* tiff = TIFFOpenW(path.c_str(), "r");
#else
    TIFF* tiff = TIFFOpen(path.c_str(), "r");
#endif
    if (tiff == nullptr)
    {
        return fail(SSC_STATUS_IO_ERROR, "TIFF could not be opened for read-only access.");
    }

    uint32_t width = 0;
    uint32_t height = 0;
    uint16_t bit_depth = 0;
    uint16_t samples_per_pixel = 0;
    uint16_t photometric = 0;
    uint16_t planar_config = PLANARCONFIG_CONTIG;
    uint16_t orientation = ORIENTATION_TOPLEFT;
    const bool has_required_fields =
        TIFFGetField(tiff, TIFFTAG_IMAGEWIDTH, &width) == 1 &&
        TIFFGetField(tiff, TIFFTAG_IMAGELENGTH, &height) == 1 &&
        TIFFGetFieldDefaulted(tiff, TIFFTAG_BITSPERSAMPLE, &bit_depth) == 1 &&
        TIFFGetFieldDefaulted(tiff, TIFFTAG_SAMPLESPERPIXEL, &samples_per_pixel) == 1 &&
        TIFFGetField(tiff, TIFFTAG_PHOTOMETRIC, &photometric) == 1;
    TIFFGetFieldDefaulted(tiff, TIFFTAG_PLANARCONFIG, &planar_config);
    TIFFGetFieldDefaulted(tiff, TIFFTAG_ORIENTATION, &orientation);

    if (!has_required_fields || width == 0 || height == 0)
    {
        TIFFClose(tiff);
        return fail(SSC_STATUS_DECODE_ERROR, "TIFF is missing required image metadata.");
    }
    if ((bit_depth != 8 && bit_depth != 16) || planar_config != PLANARCONFIG_CONTIG ||
        (orientation != ORIENTATION_TOPLEFT && orientation != ORIENTATION_BOTLEFT))
    {
        TIFFClose(tiff);
        return fail(SSC_STATUS_UNSUPPORTED, "TIFF must be 8/16-bit contiguous scanlines with a left-origin orientation.");
    }

    const bool grayscale = photometric == PHOTOMETRIC_MINISBLACK || photometric == PHOTOMETRIC_MINISWHITE;
    const bool rgb = photometric == PHOTOMETRIC_RGB;
    const uint32_t color_channels = grayscale ? 1U : (rgb ? 3U : 0U);
    uint16_t extra_count = 0;
    uint16_t* extra_types = nullptr;
    const bool has_declared_alpha =
        TIFFGetField(tiff, TIFFTAG_EXTRASAMPLES, &extra_count, &extra_types) == 1 && extra_count > 0;
    const bool had_alpha = has_declared_alpha && samples_per_pixel == color_channels + 1;
    if (color_channels == 0 ||
        (samples_per_pixel != color_channels && !had_alpha))
    {
        TIFFClose(tiff);
        return fail(SSC_STATUS_UNSUPPORTED, "TIFF channels or photometric layout are unsupported.");
    }

    const tmsize_t scanline_size = TIFFScanlineSize(tiff);
    if (scanline_size <= 0)
    {
        TIFFClose(tiff);
        return fail(SSC_STATUS_DECODE_ERROR, "TIFF scanline size is invalid.");
    }

    try
    {
        std::vector<uint8_t> scanline(static_cast<size_t>(scanline_size));
        const size_t pixel_count = static_cast<size_t>(width) * height;
        decoded.interleaved_pixels.resize(pixel_count * color_channels);
        const float denominator = bit_depth == 8 ? 255.0f : 65535.0f;
        for (uint32_t source_y = 0; source_y < height; ++source_y)
        {
            if (TIFFReadScanline(tiff, scanline.data(), source_y, 0) < 0)
            {
                TIFFClose(tiff);
                return fail(SSC_STATUS_DECODE_ERROR, "TIFF scanline data is corrupt.");
            }
            const uint32_t target_y = orientation == ORIENTATION_BOTLEFT
                ? height - source_y - 1
                : source_y;
            for (uint32_t x = 0; x < width; ++x)
            {
                for (uint32_t channel = 0; channel < color_channels; ++channel)
                {
                    const size_t source_index =
                        (static_cast<size_t>(x) * samples_per_pixel + channel) * (bit_depth / 8);
                    uint16_t value = scanline[source_index];
                    if (bit_depth == 16)
                    {
                        std::memcpy(&value, scanline.data() + source_index, sizeof(value));
                    }
                    float normalized = static_cast<float>(value) / denominator;
                    if (grayscale && photometric == PHOTOMETRIC_MINISWHITE)
                    {
                        normalized = 1.0f - normalized;
                    }
                    decoded.interleaved_pixels[
                        (static_cast<size_t>(target_y) * width + x) * color_channels + channel] = normalized;
                }
            }
        }

        decoded.descriptor.struct_size = sizeof(SSC_ImageDescriptor);
        decoded.descriptor.abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
        decoded.descriptor.width = width;
        decoded.descriptor.height = height;
        decoded.descriptor.channel_count = color_channels;
        decoded.descriptor.source_bit_depth = bit_depth;
        decoded.descriptor.color_model = grayscale ? SSC_COLOR_MODEL_GRAYSCALE : SSC_COLOR_MODEL_RGB;
        decoded.descriptor.working_pixel_format = SSC_PIXEL_FORMAT_FLOAT32_PLANAR;
        decoded.file_format = SSC_IMAGE_FILE_FORMAT_TIFF;
        decoded.had_alpha = had_alpha;
    }
    catch (const std::bad_alloc&)
    {
        TIFFClose(tiff);
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate normalized TIFF pixels.");
    }

    TIFFClose(tiff);
    return SSC_STATUS_OK;
}

bool is_cancelled(const volatile uint32_t* flag) noexcept
{
    return flag != nullptr && *flag != 0;
}

int reflected_index(int value, int length) noexcept
{
    if (length <= 1) return 0;
    while (value < 0 || value >= length)
    {
        value = value < 0 ? -value : 2 * length - value - 2;
    }
    return value;
}

bool gaussian_blur(
    const std::vector<float>& input,
    uint32_t width,
    uint32_t height,
    float sigma,
    std::vector<float>& output,
    const volatile uint32_t* cancellation_flag);

// Wavelet multiscale pyramid holding floating-point detail layers and low-pass residual.
// Intermediate matrices and calculations exclusively use IEEE 754 float.
// Detail layers resulting from subtractions (W_j = C_j - C_{j+1}) contain zero-mean
// negative and positive values that are strictly preserved without premature clamping or underflow.
struct WaveletPyramid
{
    std::vector<std::vector<float>> detail_layers; // [scale][pixel], float32 signed differences
    std::vector<float> residual;                   // [pixel], low-pass approximation C_J
    uint32_t width{0};
    uint32_t height{0};
    int num_scales{6};
};

struct WaveletLayerParams
{
    float enhancement_factor{1.0f};
    float gaussian_width{0.0f};
    float denoise{0.0f};
    float threshold{0.0f};
};

// 1. ANALYSIS: recursive normalized Gaussian decomposition.
// L0=input; Li=GaussianBlur(L(i-1), width_i); Di=L(i-1)-Li.
// Each level consumes the preceding low pass; no band is computed directly from input.
bool wavelet_decompose_gaussian(
    const std::vector<float>& input,
    uint32_t width,
    uint32_t height,
    const std::vector<WaveletLayerParams>& layer_params,
    WaveletPyramid& out_pyramid,
    const volatile uint32_t* cancellation_flag,
    const std::function<void(int scale)>& on_layer_done)
{
    out_pyramid.width = width;
    out_pyramid.height = height;
    out_pyramid.num_scales = static_cast<int>(layer_params.size());
    out_pyramid.detail_layers.clear();
    out_pyramid.detail_layers.reserve(layer_params.size());

    const size_t pixel_count = static_cast<size_t>(width) * height;
    std::vector<float> current = input;

    for (size_t scale = 0; scale < layer_params.size(); ++scale)
    {
        if (is_cancelled(cancellation_flag)) return false;

        std::vector<float> blurred;
        if (!gaussian_blur(
                current,
                width,
                height,
                layer_params[scale].gaussian_width,
                blurred,
                cancellation_flag))
        {
            return false;
        }

        // Subtraction: W_j = C_j - C_{j+1}.
        // Detail coefficients are zero-centered signed floats.
        // Negative values represent darker high frequencies/edges and MUST NOT be clamped to zero or unsigned integers!
        std::vector<float> detail(pixel_count);
        for (size_t i = 0; i < pixel_count; ++i)
        {
            detail[i] = current[i] - blurred[i];
        }

        out_pyramid.detail_layers.push_back(std::move(detail));
        current = std::move(blurred);

        if (on_layer_done)
        {
            on_layer_done(static_cast<int>(scale));
        }
    }

    out_pyramid.residual = std::move(current);
    return true;
}

// Recursive undecimated B3-spline decomposition. The existing wavelet processor
// owns both backends; changing backend changes analysis only, never slider meaning.
bool wavelet_decompose_atrous(
    const std::vector<float>& input,
    uint32_t width,
    uint32_t height,
    int num_scales,
    WaveletPyramid& out_pyramid,
    const volatile uint32_t* cancellation_flag,
    const std::function<void(int scale)>& on_layer_done)
{
    constexpr float kernel[5] = {1.0f / 16.0f, 4.0f / 16.0f, 6.0f / 16.0f, 4.0f / 16.0f, 1.0f / 16.0f};
    const size_t pixel_count = static_cast<size_t>(width) * height;
    out_pyramid.width = width;
    out_pyramid.height = height;
    out_pyramid.num_scales = num_scales;
    out_pyramid.detail_layers.clear();
    out_pyramid.detail_layers.reserve(static_cast<size_t>(num_scales));
    std::vector<float> current = input;

    for (int scale = 0; scale < num_scales; ++scale)
    {
        if (is_cancelled(cancellation_flag)) return false;
        const int spacing = 1 << scale;
        auto temporary_lease = float_buffer_pool.acquire(pixel_count);
        auto& temporary = temporary_lease.values();
        std::vector<float> blurred(pixel_count);
        for (uint32_t y = 0; y < height; ++y)
        {
            if (is_cancelled(cancellation_flag)) return false;
            for (uint32_t x = 0; x < width; ++x)
            {
                float sum = 0;
                for (int tap = -2; tap <= 2; ++tap)
                {
                    const int sample_x = reflected_index(
                        static_cast<int>(x) + tap * spacing,
                        static_cast<int>(width));
                    sum += current[static_cast<size_t>(y) * width + sample_x] * kernel[tap + 2];
                }
                temporary[static_cast<size_t>(y) * width + x] = sum;
            }
        }
        for (uint32_t y = 0; y < height; ++y)
        {
            if (is_cancelled(cancellation_flag)) return false;
            for (uint32_t x = 0; x < width; ++x)
            {
                float sum = 0;
                for (int tap = -2; tap <= 2; ++tap)
                {
                    const int sample_y = reflected_index(
                        static_cast<int>(y) + tap * spacing,
                        static_cast<int>(height));
                    sum += temporary[static_cast<size_t>(sample_y) * width + x] * kernel[tap + 2];
                }
                blurred[static_cast<size_t>(y) * width + x] = sum;
            }
        }

        std::vector<float> detail(pixel_count);
        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
            detail[pixel] = current[pixel] - blurred[pixel];
        out_pyramid.detail_layers.push_back(std::move(detail));
        current = std::move(blurred);
        if (on_layer_done) on_layer_done(scale);
    }

    out_pyramid.residual = std::move(current);
    return true;
}

// 2. MODULATION: Apply one explicit enhancement factor after optional shrinkage.
// All operations are purely in float32 without clipping. Signs are preserved.
void wavelet_modulate_details(
    WaveletPyramid& pyramid,
    const std::vector<WaveletLayerParams>& layer_params)
{
    const size_t pixel_count = static_cast<size_t>(pyramid.width) * pyramid.height;
    for (size_t scale = 0; scale < pyramid.detail_layers.size() && scale < layer_params.size(); ++scale)
    {
        auto& detail = pyramid.detail_layers[scale];
        const auto& params = layer_params[scale];
        const float soft_threshold = params.denoise;
        const float hard_threshold = params.threshold;

        for (size_t i = 0; i < pixel_count; ++i)
        {
            float val = detail[i];
            if (soft_threshold > 0.0f)
            {
                val = std::copysign(std::max(0.0f, std::abs(val) - soft_threshold), val);
            }
            if (hard_threshold > 0.0f && std::abs(val) < hard_threshold) val = 0.0f;
            detail[i] = val * params.enhancement_factor;
        }
    }
}

// 3. SYNTHESIS: Multi-scale recomposition.
// Sum = residual C_J + sum_{j=0}^{J-1} (W_j' * global_strength)
// No clamping occurs here; display and integer export own their existing conversions.
void wavelet_synthesize(
    const WaveletPyramid& pyramid,
    float global_strength,
    int solo_scale,
    std::vector<float>& output)
{
    const size_t pixel_count = static_cast<size_t>(pyramid.width) * pyramid.height;
    output.resize(pixel_count);

    if (solo_scale >= 0 && solo_scale < static_cast<int>(pyramid.detail_layers.size()))
    {
        // Solo mode: displays the isolated detail layer centered at neutral gray 0.5f
        const auto& layer = pyramid.detail_layers[static_cast<size_t>(solo_scale)];
        for (size_t i = 0; i < pixel_count; ++i)
        {
            output[i] = 0.5f + layer[i] * global_strength;
        }
    }
    else
    {
        // Standard synthesis: sum residual C_J + all modulated detail layers W_j * global_strength
        std::copy(pyramid.residual.begin(), pyramid.residual.end(), output.begin());
        for (const auto& layer : pyramid.detail_layers)
        {
            for (size_t i = 0; i < pixel_count; ++i)
            {
                output[i] += layer[i] * global_strength;
            }
        }
    }
}

bool gaussian_blur(
    const std::vector<float>& input,
    uint32_t width,
    uint32_t height,
    float sigma,
    std::vector<float>& output,
    const volatile uint32_t* cancellation_flag)
{
    sigma = std::max(0.15f, sigma);
    const int radius = std::clamp(static_cast<int>(std::ceil(sigma * 3.0f)), 1, 64);
    std::vector<float> kernel(static_cast<size_t>(radius * 2 + 1));
    float total = 0;
    for (int index = -radius; index <= radius; ++index)
    {
        const float value = std::exp(-0.5f * static_cast<float>(index * index) / (sigma * sigma));
        kernel[static_cast<size_t>(index + radius)] = value;
        total += value;
    }
    for (float& value : kernel) value /= total;

    const size_t count = static_cast<size_t>(width) * height;
    auto temporary_lease = float_buffer_pool.acquire(count);
    auto& temporary = temporary_lease.values();
    output.resize(count);
    for (uint32_t y = 0; y < height; ++y)
    {
        if (is_cancelled(cancellation_flag)) return false;
        for (uint32_t x = 0; x < width; ++x)
        {
            float sum = 0;
            for (int offset = -radius; offset <= radius; ++offset)
            {
                const int sample_x = reflected_index(static_cast<int>(x) + offset, static_cast<int>(width));
                sum += input[static_cast<size_t>(y) * width + sample_x] * kernel[static_cast<size_t>(offset + radius)];
            }
            temporary[static_cast<size_t>(y) * width + x] = sum;
        }
    }
    for (uint32_t y = 0; y < height; ++y)
    {
        if (is_cancelled(cancellation_flag)) return false;
        for (uint32_t x = 0; x < width; ++x)
        {
            float sum = 0;
            for (int offset = -radius; offset <= radius; ++offset)
            {
                const int sample_y = reflected_index(static_cast<int>(y) + offset, static_cast<int>(height));
                sum += temporary[static_cast<size_t>(sample_y) * width + x] * kernel[static_cast<size_t>(offset + radius)];
            }
            output[static_cast<size_t>(y) * width + x] = sum;
        }
    }
    return true;
}

float median_value(std::vector<float> values)
{
    if (values.empty()) return 0.0f;
    const size_t middle_index = values.size() / 2;
    auto middle = values.begin() + static_cast<ptrdiff_t>(middle_index);
    std::nth_element(values.begin(), middle, values.end());
    const float upper = *middle;
    if ((values.size() & 1U) != 0) return upper;
    const auto lower = std::max_element(values.begin(), middle);
    return 0.5f * (upper + *lower);
}

float estimate_noise_mad(const std::vector<float>& finest_detail)
{
    if (finest_detail.empty()) return 0.0f;
    const float center = median_value(finest_detail);
    std::vector<float> deviations(finest_detail.size());
    std::transform(
        finest_detail.begin(), finest_detail.end(), deviations.begin(),
        [center](float value) { return std::abs(value - center); });
    return median_value(std::move(deviations)) / 0.67448975f;
}

float soft_threshold(float value, float threshold) noexcept
{
    return std::copysign(std::max(0.0f, std::abs(value) - threshold), value);
}

bool wavelet_shrink_channel(
    const std::vector<float>& source,
    uint32_t width,
    uint32_t height,
    float strength,
    float detail_protection,
    float threshold_scale,
    std::vector<float>& output,
    const volatile uint32_t* cancellation_flag)
{
    if (strength <= 0.0f)
    {
        output = source;
        return true;
    }

    constexpr int scale_count = 3;
    std::vector<std::vector<float>> details;
    details.reserve(scale_count);
    std::vector<float> current = source;
    for (int scale = 0; scale < scale_count; ++scale)
    {
        std::vector<float> low_pass;
        if (!gaussian_blur(current, width, height, static_cast<float>(1 << scale), low_pass, cancellation_flag))
            return false;
        std::vector<float> detail(source.size());
        for (size_t pixel = 0; pixel < source.size(); ++pixel)
            detail[pixel] = current[pixel] - low_pass[pixel];
        details.push_back(std::move(detail));
        current = std::move(low_pass);
    }

    constexpr float epsilon = 1e-6f;
    const float noise_sigma = std::max(estimate_noise_mad(details.front()), epsilon);
    output = current;
    for (int scale = 0; scale < scale_count; ++scale)
    {
        const float threshold = strength * threshold_scale * noise_sigma;
        for (size_t pixel = 0; pixel < source.size(); ++pixel)
        {
            const float coefficient = details[static_cast<size_t>(scale)][pixel];
            const float protection = detail_protection * std::clamp(
                std::abs(coefficient) / (3.0f * threshold + epsilon), 0.0f, 1.0f);
            output[pixel] += soft_threshold(coefficient, threshold * (1.0f - protection));
        }
    }
    return true;
}

using Complex = std::complex<float>;

uint32_t next_power_of_two(uint32_t value) noexcept
{
    uint32_t result = 1;
    while (result < value && result <= (std::numeric_limits<uint32_t>::max() >> 1U)) result <<= 1U;
    return result;
}

void fft_1d(std::vector<Complex>& values, bool inverse)
{
    const size_t count = values.size();
    for (size_t index = 1, reversed = 0; index < count; ++index)
    {
        size_t bit = count >> 1U;
        for (; (reversed & bit) != 0; bit >>= 1U) reversed ^= bit;
        reversed ^= bit;
        if (index < reversed) std::swap(values[index], values[reversed]);
    }
    constexpr float pi = 3.14159265358979323846f;
    for (size_t length = 2; length <= count; length <<= 1U)
    {
        const float angle = 2.0f * pi / static_cast<float>(length) * (inverse ? 1.0f : -1.0f);
        const Complex root(std::cos(angle), std::sin(angle));
        for (size_t start = 0; start < count; start += length)
        {
            Complex factor(1.0f, 0.0f);
            for (size_t offset = 0; offset < length / 2; ++offset)
            {
                const Complex even = values[start + offset];
                const Complex odd = values[start + offset + length / 2] * factor;
                values[start + offset] = even + odd;
                values[start + offset + length / 2] = even - odd;
                factor *= root;
            }
        }
    }
    if (inverse)
    {
        const float reciprocal = 1.0f / static_cast<float>(count);
        for (Complex& value : values) value *= reciprocal;
    }
}

bool fft_2d(
    std::vector<Complex>& values,
    uint32_t width,
    uint32_t height,
    bool inverse,
    const volatile uint32_t* cancellation_flag)
{
    std::vector<Complex> line(std::max(width, height));
    for (uint32_t y = 0; y < height; ++y)
    {
        if (is_cancelled(cancellation_flag)) return false;
        line.resize(width);
        std::copy_n(values.begin() + static_cast<ptrdiff_t>(static_cast<size_t>(y) * width), width, line.begin());
        fft_1d(line, inverse);
        std::copy_n(line.begin(), width, values.begin() + static_cast<ptrdiff_t>(static_cast<size_t>(y) * width));
    }
    for (uint32_t x = 0; x < width; ++x)
    {
        if (is_cancelled(cancellation_flag)) return false;
        line.resize(height);
        for (uint32_t y = 0; y < height; ++y) line[y] = values[static_cast<size_t>(y) * width + x];
        fft_1d(line, inverse);
        for (uint32_t y = 0; y < height; ++y) values[static_cast<size_t>(y) * width + x] = line[y];
    }
    return true;
}

std::pair<float, float> phase_correlation_shift(
    const std::vector<float>& reference,
    const std::vector<float>& moving,
    uint32_t width,
    uint32_t height,
    int search,
    const volatile uint32_t* cancellation_flag)
{
    const uint32_t fft_width = next_power_of_two(width);
    const uint32_t fft_height = next_power_of_two(height);
    const size_t fft_count = static_cast<size_t>(fft_width) * fft_height;
    std::vector<Complex> reference_frequency(fft_count);
    std::vector<Complex> moving_frequency(fft_count);
    double reference_sum = 0;
    double moving_sum = 0;
    for (float value : reference) reference_sum += value;
    for (float value : moving) moving_sum += value;
    const float reference_mean = static_cast<float>(reference_sum / reference.size());
    const float moving_mean = static_cast<float>(moving_sum / moving.size());
    for (uint32_t y = 0; y < height; ++y)
    for (uint32_t x = 0; x < width; ++x)
    {
        const size_t source_index = static_cast<size_t>(y) * width + x;
        const size_t fft_index = static_cast<size_t>(y) * fft_width + x;
        reference_frequency[fft_index] = Complex(reference[source_index] - reference_mean, 0);
        moving_frequency[fft_index] = Complex(moving[source_index] - moving_mean, 0);
    }
    if (!fft_2d(reference_frequency, fft_width, fft_height, false, cancellation_flag) ||
        !fft_2d(moving_frequency, fft_width, fft_height, false, cancellation_flag))
        return {0.0f, 0.0f};

    constexpr float epsilon = 1e-12f;
    for (size_t index = 0; index < fft_count; ++index)
    {
        const Complex cross = moving_frequency[index] * std::conj(reference_frequency[index]);
        moving_frequency[index] = cross / (std::abs(cross) + epsilon);
    }
    if (!fft_2d(moving_frequency, fft_width, fft_height, true, cancellation_flag)) return {0.0f, 0.0f};

    float peak_value = -std::numeric_limits<float>::infinity();
    int peak_x = 0;
    int peak_y = 0;
    for (int dy = -search; dy <= search; ++dy)
    for (int dx = -search; dx <= search; ++dx)
    {
        const uint32_t wrapped_x = dx < 0 ? fft_width - static_cast<uint32_t>(-dx) : static_cast<uint32_t>(dx);
        const uint32_t wrapped_y = dy < 0 ? fft_height - static_cast<uint32_t>(-dy) : static_cast<uint32_t>(dy);
        const float value = moving_frequency[static_cast<size_t>(wrapped_y) * fft_width + wrapped_x].real();
        if (value > peak_value)
        {
            peak_value = value;
            peak_x = dx;
            peak_y = dy;
        }
    }

    const auto correlation = [&](int x, int y)
    {
        const int wrapped_x = (x % static_cast<int>(fft_width) + static_cast<int>(fft_width)) % static_cast<int>(fft_width);
        const int wrapped_y = (y % static_cast<int>(fft_height) + static_cast<int>(fft_height)) % static_cast<int>(fft_height);
        return moving_frequency[static_cast<size_t>(wrapped_y) * fft_width + wrapped_x].real();
    };
    const auto refine = [](float before, float center, float after)
    {
        const float denominator = before - 2.0f * center + after;
        return std::abs(denominator) <= 1e-12f
            ? 0.0f
            : std::clamp(0.5f * (before - after) / denominator, -0.5f, 0.5f);
    };
    float refined_x = static_cast<float>(peak_x) +
        refine(correlation(peak_x - 1, peak_y), peak_value, correlation(peak_x + 1, peak_y));
    float refined_y = static_cast<float>(peak_y) +
        refine(correlation(peak_x, peak_y - 1), peak_value, correlation(peak_x, peak_y + 1));
    // Exact integer translations should remain exact. This also removes the tiny
    // parabolic-fit bias caused by FFT roundoff on impulse-like calibration data.
    if (std::abs(refined_x - std::round(refined_x)) < 1e-3f) refined_x = std::round(refined_x);
    if (std::abs(refined_y - std::round(refined_y)) < 1e-3f) refined_y = std::round(refined_y);
    return {refined_x, refined_y};
}

float sinc(float value) noexcept
{
    constexpr float pi = 3.14159265358979323846f;
    if (std::abs(value) < 1e-6f) return 1.0f;
    if (std::abs(value - std::round(value)) < 1e-6f) return 0.0f;
    const float argument = pi * value;
    return std::sin(argument) / argument;
}

float lanczos3_sample(
    const std::vector<float>& channel,
    uint32_t width,
    uint32_t height,
    float x,
    float y) noexcept
{
    const int center_x = static_cast<int>(std::floor(x));
    const int center_y = static_cast<int>(std::floor(y));
    float weighted_sum = 0;
    float total_weight = 0;
    for (int sample_y = center_y - 2; sample_y <= center_y + 3; ++sample_y)
    {
        const float distance_y = y - static_cast<float>(sample_y);
        const float weight_y = std::abs(distance_y) < 3.0f ? sinc(distance_y) * sinc(distance_y / 3.0f) : 0.0f;
        for (int sample_x = center_x - 2; sample_x <= center_x + 3; ++sample_x)
        {
            const float distance_x = x - static_cast<float>(sample_x);
            const float weight_x = std::abs(distance_x) < 3.0f ? sinc(distance_x) * sinc(distance_x / 3.0f) : 0.0f;
            const float weight = weight_x * weight_y;
            const int reflected_x = reflected_index(sample_x, static_cast<int>(width));
            const int reflected_y = reflected_index(sample_y, static_cast<int>(height));
            weighted_sum += channel[static_cast<size_t>(reflected_y) * width + reflected_x] * weight;
            total_weight += weight;
        }
    }
    return std::abs(total_weight) <= 1e-8f ? 0.0f : weighted_sum / total_weight;
}
}

struct SSC_Image
{
    std::shared_ptr<const ImageStorage> storage;
    uint64_t instance_identity{};
    bool is_master{};
};

struct SSC_Pipeline
{
    std::shared_ptr<const ImageStorage> immutable_master;
    std::shared_ptr<const ImageStorage> working_output;
    uint64_t output_identity{};
};

struct starsim_core_context
{
    uint32_t abi_version;
};

extern "C" uint32_t starsim_core_native_abi_version(void) noexcept
{
    return STARSIM_CORE_NATIVE_ABI_VERSION;
}

extern "C" int32_t starsim_core_context_create(starsim_core_context** out_context) noexcept
{
    if (out_context == nullptr)
    {
        return SSC_STATUS_INVALID_ARGUMENT;
    }

    *out_context = new (std::nothrow) starsim_core_context{
        STARSIM_CORE_NATIVE_ABI_VERSION};

    return *out_context == nullptr
        ? SSC_STATUS_OUT_OF_MEMORY
        : SSC_STATUS_OK;
}

extern "C" void starsim_core_context_destroy(starsim_core_context* context) noexcept
{
    delete context;
}

extern "C" void ssc_buffer_pool_configure(uint64_t maximum_retained_bytes) noexcept
{
    float_buffer_pool.configure(maximum_retained_bytes);
}

extern "C" void ssc_buffer_pool_trim(void) noexcept
{
    float_buffer_pool.trim();
}

extern "C" void ssc_buffer_pool_get_metrics(
    uint64_t* retained_bytes,
    uint32_t* retained_buffer_count,
    uint64_t* reuse_count,
    uint64_t* allocation_count) noexcept
{
    uint64_t bytes = 0;
    uint32_t count = 0;
    uint64_t reuses = 0;
    uint64_t allocations = 0;
    float_buffer_pool.metrics(bytes, count, reuses, allocations);
    if (retained_bytes != nullptr) *retained_bytes = bytes;
    if (retained_buffer_count != nullptr) *retained_buffer_count = count;
    if (reuse_count != nullptr) *reuse_count = reuses;
    if (allocation_count != nullptr) *allocation_count = allocations;
}

extern "C" SSC_Status ssc_get_last_error_utf8(
    char* buffer,
    uint32_t buffer_size,
    uint32_t* required_size) noexcept
{
    if (required_size == nullptr)
    {
        return SSC_STATUS_INVALID_ARGUMENT;
    }

    const size_t required = last_error.size() + 1;
    if (required > std::numeric_limits<uint32_t>::max())
    {
        return SSC_STATUS_INTERNAL_ERROR;
    }

    *required_size = static_cast<uint32_t>(required);
    if (buffer == nullptr || buffer_size == 0)
    {
        return SSC_STATUS_OK;
    }

    if (buffer_size < required)
    {
        return SSC_STATUS_BUFFER_TOO_SMALL;
    }

    std::memcpy(buffer, last_error.c_str(), required);
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_create_master_f32(
    const SSC_ImageDescriptor* descriptor,
    const float* interleaved_pixels,
    uint64_t pixel_value_count,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Output image handle pointer is null.");
    }
    *out_image = nullptr;

    const SSC_Status validation = validate_descriptor(descriptor, pixel_value_count);
    if (validation != SSC_STATUS_OK)
    {
        return validation;
    }

    if (interleaved_pixels == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Input pixel pointer is null.");
    }

    try
    {
        auto storage = std::make_shared<ImageStorage>();
        storage->descriptor = *descriptor;
        std::fill(
            std::begin(storage->descriptor.reserved),
            std::end(storage->descriptor.reserved),
            uint64_t{0});
        storage->source_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        storage->planar_pixels.resize(static_cast<size_t>(pixel_value_count));

        const size_t pixel_count =
            static_cast<size_t>(descriptor->width) * descriptor->height;
        const size_t channel_count = descriptor->channel_count;
        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
        {
            for (size_t channel = 0; channel < channel_count; ++channel)
            {
                storage->planar_pixels[channel * pixel_count + pixel] =
                    interleaved_pixels[pixel * channel_count + channel];
            }
        }

        auto image = std::make_unique<SSC_Image>();
        image->storage = std::move(storage);
        image->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        image->is_master = true;
        *out_image = image.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate native image storage.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected failure while creating native image.");
    }
}

extern "C" SSC_Status ssc_image_create_master_planar_f32(
    const SSC_ImageDescriptor* descriptor,
    const float* const* planes,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (out_image == nullptr || planes == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Output image handle or planes pointer is null.");
    }
    *out_image = nullptr;

    uint64_t expected_values = 0;
    if (!checked_value_count(*descriptor, expected_values))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image descriptor dimensions overflow.");
    }

    const SSC_Status validation = validate_descriptor(descriptor, expected_values);
    if (validation != SSC_STATUS_OK)
    {
        return validation;
    }

    try
    {
        auto storage = std::make_shared<ImageStorage>();
        storage->descriptor = *descriptor;
        std::fill(
            std::begin(storage->descriptor.reserved),
            std::end(storage->descriptor.reserved),
            uint64_t{0});
        storage->source_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        storage->planar_pixels.resize(static_cast<size_t>(expected_values));

        const size_t pixel_count =
            static_cast<size_t>(descriptor->width) * descriptor->height;
        for (size_t c = 0; c < descriptor->channel_count; ++c)
        {
            if (planes[c] == nullptr)
            {
                return fail(SSC_STATUS_INVALID_ARGUMENT, "Plane buffer pointer is null.");
            }
            std::memcpy(
                storage->planar_pixels.data() + c * pixel_count,
                planes[c],
                pixel_count * sizeof(float));
        }

        auto instance = std::make_unique<SSC_Image>();
        instance->storage = std::move(storage);
        instance->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        instance->is_master = true;
        *out_image = instance.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate native image storage.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected failure while creating planar master image.");
    }
}

extern "C" SSC_Status ssc_image_load_file_utf8(
    const char* canonical_path_utf8,
    SSC_ImageFileInfo* out_file_info,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (canonical_path_utf8 == nullptr || out_file_info == nullptr || out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image path, file info, or output handle is null.");
    }
    *out_image = nullptr;
    if (out_file_info->struct_size < sizeof(SSC_ImageFileInfo))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image file info struct_size is too small.");
    }
    if (out_file_info->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Image file info ABI version is not supported.");
    }

#if defined(_WIN32)
    std::wstring path;
    if (!utf8_to_wide(canonical_path_utf8, path))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image path is not valid UTF-8.");
    }
#else
    std::string path(canonical_path_utf8);
#endif

    FILE* signature_file = open_read_only(path);
    if (signature_file == nullptr)
    {
        return fail(SSC_STATUS_IO_ERROR, "Image could not be opened for read-only access.");
    }
    uint8_t signature[8]{};
    const size_t signature_size = std::fread(signature, 1, sizeof(signature), signature_file);
    std::fclose(signature_file);

    DecodedImage decoded;
    SSC_Status status = SSC_STATUS_UNSUPPORTED;
    if (signature_size == sizeof(signature) && png_sig_cmp(signature, 0, sizeof(signature)) == 0)
    {
        status = decode_png(path, decoded);
    }
    else if (signature_size >= 4 &&
        ((signature[0] == 'I' && signature[1] == 'I' && signature[2] == 42 && signature[3] == 0) ||
         (signature[0] == 'M' && signature[1] == 'M' && signature[2] == 0 && signature[3] == 42)))
    {
        status = decode_tiff(path, decoded);
    }
    else
    {
        return fail(SSC_STATUS_UNSUPPORTED, "Only PNG and TIFF image signatures are supported.");
    }
    if (status != SSC_STATUS_OK)
    {
        return status;
    }

    status = ssc_image_create_master_f32(
        &decoded.descriptor,
        decoded.interleaved_pixels.data(),
        decoded.interleaved_pixels.size(),
        out_image);
    if (status != SSC_STATUS_OK)
    {
        return status;
    }

    *out_file_info = SSC_ImageFileInfo{};
    out_file_info->struct_size = sizeof(SSC_ImageFileInfo);
    out_file_info->abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
    out_file_info->file_format = decoded.file_format;
    out_file_info->had_alpha = decoded.had_alpha ? 1U : 0U;
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_clone_working(
    SSC_ImageHandle source,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (source == nullptr || out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Source or output image handle is null.");
    }
    *out_image = nullptr;

    try
    {
        auto storage = std::make_shared<ImageStorage>(*source->storage);
        auto image = std::make_unique<SSC_Image>();
        image->storage = std::move(storage);
        image->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        image->is_master = false;
        *out_image = image.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to clone native image storage.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected failure while cloning native image.");
    }
}

extern "C" SSC_Status ssc_image_create_scaled_preview(
    SSC_ImageHandle source,
    float resolution_scale,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (source == nullptr || out_image == nullptr || !std::isfinite(resolution_scale) ||
        resolution_scale <= 0.0f || resolution_scale > 1.0f)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Scaled preview arguments are invalid.");
    }
    *out_image = nullptr;
    if (is_cancelled(cancellation_flag))
        return fail(SSC_STATUS_CANCELLED, "Scaled preview creation was cancelled.");
    try
    {
        const auto& input = *source->storage;
        const uint32_t source_width = input.descriptor.width;
        const uint32_t source_height = input.descriptor.height;
        const uint32_t channels = input.descriptor.channel_count;
        const uint32_t target_width = std::max(1U, static_cast<uint32_t>(std::lround(source_width * resolution_scale)));
        const uint32_t target_height = std::max(1U, static_cast<uint32_t>(std::lround(source_height * resolution_scale)));
        auto storage = std::make_shared<ImageStorage>();
        storage->descriptor = input.descriptor;
        storage->descriptor.width = target_width;
        storage->descriptor.height = target_height;
        storage->source_identity = input.source_identity;
        const size_t source_pixels = static_cast<size_t>(source_width) * source_height;
        const size_t target_pixels = static_cast<size_t>(target_width) * target_height;
        storage->planar_pixels.resize(target_pixels * channels);
        for (uint32_t channel = 0; channel < channels; ++channel)
        {
            const size_t source_offset = static_cast<size_t>(channel) * source_pixels;
            const size_t target_offset = static_cast<size_t>(channel) * target_pixels;
            for (uint32_t y = 0; y < target_height; ++y)
            {
                if (is_cancelled(cancellation_flag))
                    return fail(SSC_STATUS_CANCELLED, "Scaled preview creation was cancelled.");
                const float source_y = ((static_cast<float>(y) + 0.5f) * source_height / target_height) - 0.5f;
                const int y0 = std::clamp(static_cast<int>(std::floor(source_y)), 0, static_cast<int>(source_height) - 1);
                const int y1 = std::min(y0 + 1, static_cast<int>(source_height) - 1);
                const float fy = std::clamp(source_y - std::floor(source_y), 0.0f, 1.0f);
                for (uint32_t x = 0; x < target_width; ++x)
                {
                    const float source_x = ((static_cast<float>(x) + 0.5f) * source_width / target_width) - 0.5f;
                    const int x0 = std::clamp(static_cast<int>(std::floor(source_x)), 0, static_cast<int>(source_width) - 1);
                    const int x1 = std::min(x0 + 1, static_cast<int>(source_width) - 1);
                    const float fx = std::clamp(source_x - std::floor(source_x), 0.0f, 1.0f);
                    const float top = input.planar_pixels[source_offset + static_cast<size_t>(y0) * source_width + x0] * (1.0f - fx) +
                        input.planar_pixels[source_offset + static_cast<size_t>(y0) * source_width + x1] * fx;
                    const float bottom = input.planar_pixels[source_offset + static_cast<size_t>(y1) * source_width + x0] * (1.0f - fx) +
                        input.planar_pixels[source_offset + static_cast<size_t>(y1) * source_width + x1] * fx;
                    storage->planar_pixels[target_offset + static_cast<size_t>(y) * target_width + x] = top * (1.0f - fy) + bottom * fy;
                }
            }
        }
        auto image = std::make_unique<SSC_Image>();
        image->storage = std::move(storage);
        image->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        image->is_master = false;
        *out_image = image.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate scaled preview storage.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected scaled preview failure.");
    }
}

extern "C" void ssc_image_release(SSC_ImageHandle image) noexcept
{
    delete image;
}

extern "C" SSC_Status ssc_image_get_descriptor(
    SSC_ImageHandle image,
    SSC_ImageDescriptor* out_descriptor) noexcept
{
    clear_error();
    if (image == nullptr || out_descriptor == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image or output descriptor is null.");
    }

    if (out_descriptor->struct_size < sizeof(SSC_ImageDescriptor))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Output descriptor struct_size is too small.");
    }

    if (out_descriptor->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Output descriptor ABI version is not supported.");
    }

    *out_descriptor = image->storage->descriptor;
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_get_identity(
    SSC_ImageHandle image,
    uint64_t* out_source_identity,
    uint64_t* out_instance_identity,
    uint8_t* out_is_master) noexcept
{
    clear_error();
    if (image == nullptr || out_source_identity == nullptr ||
        out_instance_identity == nullptr || out_is_master == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image identity output argument is null.");
    }

    *out_source_identity = image->storage->source_identity;
    *out_instance_identity = image->instance_identity;
    *out_is_master = image->is_master ? uint8_t{1} : uint8_t{0};
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_render_bgra8(
    SSC_ImageHandle image,
    uint8_t* destination,
    uint64_t destination_size,
    uint32_t destination_stride) noexcept
{
    clear_error();
    if (image == nullptr || destination == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image or BGRA8 destination is null.");
    }

    const auto& descriptor = image->storage->descriptor;
    const uint64_t minimum_stride = static_cast<uint64_t>(descriptor.width) * 4;
    if (destination_stride < minimum_stride)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "BGRA8 destination stride is too small.");
    }

    const uint64_t required_size =
        static_cast<uint64_t>(destination_stride) * descriptor.height;
    if (destination_size < required_size)
    {
        return fail(SSC_STATUS_BUFFER_TOO_SMALL, "BGRA8 destination buffer is too small.");
    }

    const size_t pixel_count =
        static_cast<size_t>(descriptor.width) * descriptor.height;
    const auto to_byte = [](float value) noexcept -> uint8_t
    {
        const float clamped = std::clamp(value, 0.0f, 1.0f);
        return static_cast<uint8_t>(std::lround(clamped * 255.0f));
    };

    for (uint32_t y = 0; y < descriptor.height; ++y)
    {
        uint8_t* row = destination + static_cast<size_t>(y) * destination_stride;
        for (uint32_t x = 0; x < descriptor.width; ++x)
        {
            const size_t pixel = static_cast<size_t>(y) * descriptor.width + x;
            uint8_t red{};
            uint8_t green{};
            uint8_t blue{};
            if (descriptor.channel_count == 1)
            {
                red = green = blue = to_byte(image->storage->planar_pixels[pixel]);
            }
            else
            {
                red = to_byte(image->storage->planar_pixels[pixel]);
                green = to_byte(image->storage->planar_pixels[pixel_count + pixel]);
                blue = to_byte(image->storage->planar_pixels[2 * pixel_count + pixel]);
            }

            row[x * 4] = blue;
            row[x * 4 + 1] = green;
            row[x * 4 + 2] = red;
            row[x * 4 + 3] = UINT8_MAX;
        }
    }

    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_process(
    SSC_ImageHandle input,
    const SSC_ProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (input == nullptr || parameters == nullptr || out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Process input, parameters, or output is null.");
    }
    *out_image = nullptr;
    if (parameters->struct_size < sizeof(SSC_ProcessorParameters))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Processor parameter struct_size is too small.");
    }
    if (parameters->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Processor parameter ABI version is not supported.");
    }
    if (parameters->quality != SSC_PROCESSING_QUALITY_INTERACTIVE_PREVIEW &&
        parameters->quality != SSC_PROCESSING_QUALITY_FULL_RESOLUTION)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Processing quality is invalid.");
    }
    if (!std::isfinite(parameters->resolution_scale) || parameters->resolution_scale <= 0.0f ||
        !std::isfinite(parameters->value0) || !std::isfinite(parameters->value1) ||
        !std::isfinite(parameters->value2) || !std::isfinite(parameters->value3))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Processor parameters must be finite and resolution scale positive.");
    }
    if (cancellation_flag != nullptr && *cancellation_flag != 0)
    {
        return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
    }

    try
    {
        auto storage = std::make_shared<ImageStorage>(*input->storage);
        const size_t pixel_count =
            static_cast<size_t>(storage->descriptor.width) * storage->descriptor.height;
        const size_t channels = storage->descriptor.channel_count;
        const auto cancelled = [cancellation_flag]() noexcept
        {
            return cancellation_flag != nullptr && *cancellation_flag != 0;
        };

        if (parameters->enabled != 0)
        {
            switch (parameters->processor_kind)
            {
            case SSC_PROCESSOR_LINEAR_EXPOSURE:
            {
                const float gain = std::exp2(parameters->value0);
                for (size_t index = 0; index < storage->planar_pixels.size(); ++index)
                {
                    if ((index & 0xFFFFU) == 0 && cancelled())
                    {
                        return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
                    }
                    storage->planar_pixels[index] *= gain;
                }
                break;
            }
            case SSC_PROCESSOR_CONTRAST:
            {
                const float factor = parameters->value0;
                for (size_t index = 0; index < storage->planar_pixels.size(); ++index)
                {
                    if ((index & 0xFFFFU) == 0 && cancelled())
                    {
                        return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
                    }
                    float& value = storage->planar_pixels[index];
                    value = (value - 0.5f) * factor + 0.5f;
                }
                break;
            }
            case SSC_PROCESSOR_GAMMA:
            {
                if (parameters->value0 <= 0.0f)
                {
                    return fail(SSC_STATUS_INVALID_ARGUMENT, "Gamma must be greater than zero.");
                }
                const float exponent = 1.0f / parameters->value0;
                for (size_t index = 0; index < storage->planar_pixels.size(); ++index)
                {
                    if ((index & 0xFFFFU) == 0 && cancelled())
                    {
                        return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
                    }
                    float& value = storage->planar_pixels[index];
                    value = std::copysign(std::pow(std::abs(value), exponent), value);
                }
                break;
            }
            case SSC_PROCESSOR_RGB_BALANCE:
            {
                if (channels == 3)
                {
                    const float gains[3] = {parameters->value0, parameters->value1, parameters->value2};
                    for (size_t channel = 0; channel < channels; ++channel)
                    {
                        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                        {
                            if ((pixel & 0xFFFFU) == 0 && cancelled())
                            {
                                return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
                            }
                            storage->planar_pixels[channel * pixel_count + pixel] *= gains[channel];
                        }
                    }
                }
                break;
            }
            case SSC_PROCESSOR_SATURATION:
            {
                if (channels == 3)
                {
                    for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                    {
                        if ((pixel & 0xFFFFU) == 0 && cancelled())
                        {
                            return fail(SSC_STATUS_CANCELLED, "Processing was cancelled.");
                        }
                        float& red = storage->planar_pixels[pixel];
                        float& green = storage->planar_pixels[pixel_count + pixel];
                        float& blue = storage->planar_pixels[2 * pixel_count + pixel];
                        const float luminance = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
                        red = luminance + (red - luminance) * parameters->value0;
                        green = luminance + (green - luminance) * parameters->value0;
                        blue = luminance + (blue - luminance) * parameters->value0;
                    }
                }
                break;
            }
            default:
                return fail(SSC_STATUS_UNSUPPORTED, "Processor kind is not supported.");
            }
        }

        auto image = std::make_unique<SSC_Image>();
        image->storage = std::move(storage);
        image->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        image->is_master = false;
        *out_image = image.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate processor output.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected processor failure.");
    }
}

extern "C" SSC_Status ssc_image_process_expert(
    SSC_ImageHandle input,
    const SSC_ExpertProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) noexcept
{
    return ssc_image_process_expert_with_progress(
        input,
        parameters,
        cancellation_flag,
        nullptr,
        nullptr,
        out_image);
}

extern "C" SSC_Status ssc_image_process_expert_with_progress(
    SSC_ImageHandle input,
    const SSC_ExpertProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ProgressCallback progress_callback,
    void* progress_context,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (input == nullptr || parameters == nullptr || out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Expert process input, parameters, or output is null.");
    }
    *out_image = nullptr;
    if (parameters->struct_size < sizeof(SSC_ExpertProcessorParameters) ||
        parameters->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Expert processor ABI layout is not supported.");
    }
    if (parameters->value_count > SSC_EXPERT_PARAMETER_VALUE_COUNT ||
        parameters->quality < SSC_PROCESSING_QUALITY_INTERACTIVE_PREVIEW ||
        parameters->quality > SSC_PROCESSING_QUALITY_FULL_RESOLUTION ||
        !std::isfinite(parameters->resolution_scale) || parameters->resolution_scale <= 0)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Expert processor count, quality, or resolution scale is invalid.");
    }
    for (uint32_t index = 0; index < parameters->value_count; ++index)
    {
        if (!std::isfinite(parameters->values[index]))
        {
            return fail(SSC_STATUS_INVALID_ARGUMENT, "Expert processor values must be finite.");
        }
    }
    if (is_cancelled(cancellation_flag))
    {
        return fail(SSC_STATUS_CANCELLED, "Expert processing was cancelled.");
    }

    const auto report_progress = [&](SSC_ProgressStage stage, uint32_t current, uint32_t total) noexcept
    {
        if (progress_callback == nullptr) return;
        SSC_ProgressInfo progress{};
        progress.struct_size = sizeof(SSC_ProgressInfo);
        progress.abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
        progress.stage = stage;
        progress.current_step = current;
        progress.total_steps = total;
        progress.fraction = total == 0
            ? -1.0f
            : std::clamp(static_cast<float>(current) / static_cast<float>(total), 0.0f, 1.0f);
        progress_callback(&progress, progress_context);
    };
    report_progress(SSC_PROGRESS_STAGE_PROCESSOR, 0, 0);

    uint32_t required_values = 0;
    switch (parameters->processor_kind)
    {
    case SSC_PROCESSOR_WAVELET:
        required_values = parameters->value_count == 28 ? 28 :
            (parameters->value_count == 30 ? 30 : 31);
        break;
    case SSC_PROCESSOR_UNSHARP_MASK: required_values = 3; break;
    case SSC_PROCESSOR_MULTI_SCALE_SHARPEN:
        required_values = parameters->value_count == 4 ? 4 : 5;
        break;
    case SSC_PROCESSOR_NOISE_REDUCTION: required_values = 4; break;
    case SSC_PROCESSOR_DERINGING: required_values = 3; break;
    case SSC_PROCESSOR_RICHARDSON_LUCY:
        required_values = parameters->value_count == 5 ? 5 : 4;
        break;
    case SSC_PROCESSOR_RGB_ALIGN: required_values = 8; break;
    case SSC_PROCESSOR_ADVANCED_COLOR: required_values = 6; break;
    case SSC_PROCESSOR_ADVANCED_TONE: required_values = 5; break;
    case SSC_PROCESSOR_LOCAL_DETAIL: required_values = 5; break;
    default: return fail(SSC_STATUS_UNSUPPORTED, "Expert processor kind is not supported.");
    }
    if (parameters->value_count != required_values)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Expert processor value count does not match its schema.");
    }

    try
    {
        auto storage = std::make_shared<ImageStorage>(*input->storage);
        const uint32_t width = storage->descriptor.width;
        const uint32_t height = storage->descriptor.height;
        const uint32_t channels = storage->descriptor.channel_count;
        const size_t pixel_count = static_cast<size_t>(width) * height;
        const auto read_channel = [&](uint32_t channel)
        {
            const auto begin = storage->planar_pixels.begin() + static_cast<ptrdiff_t>(channel * pixel_count);
            return std::vector<float>(begin, begin + static_cast<ptrdiff_t>(pixel_count));
        };
        const auto write_channel = [&](uint32_t channel, const std::vector<float>& values)
        {
            std::copy(values.begin(), values.end(),
                storage->planar_pixels.begin() + static_cast<ptrdiff_t>(channel * pixel_count));
        };

        if (parameters->enabled != 0)
        {
            switch (parameters->processor_kind)
            {
            case SSC_PROCESSOR_WAVELET:
            {
                const float global_strength = parameters->values[0];
                const float initial_layer_width = parameters->values[1];
                const bool linked = parameters->values[2] >= 0.5f;
                const int solo = static_cast<int>(std::lround(parameters->values[27]));
                const bool legacy_parameters = parameters->value_count == 28;
                const int scale_scheme = legacy_parameters
                    ? 1
                    : static_cast<int>(std::lround(parameters->values[28]));
                const float step_increment = legacy_parameters ? 1.0f : parameters->values[29];
                const int decomposition_backend = parameters->value_count >= 31
                    ? static_cast<int>(std::lround(parameters->values[30]))
                    : 0;
                const uint32_t total_steps = channels * 6U;
                uint32_t completed_steps = 0;

                std::vector<WaveletLayerParams> layer_params(6);
                for (int scale = 0; scale < 6; ++scale)
                {
                    const size_t parameter_base = static_cast<size_t>(3 + (linked ? 0 : scale * 4));
                    const float legacy_sharpen = legacy_parameters ? parameters->values[parameter_base + 1] : 0.0f;
                    layer_params[scale].enhancement_factor =
                        parameters->values[parameter_base] * (1.0f + legacy_sharpen);
                    layer_params[scale].denoise = parameters->values[parameter_base + 2];
                    layer_params[scale].threshold = parameters->values[parameter_base + 3];

                    const float configured_override = legacy_parameters
                        ? 0.0f
                        : parameters->values[parameter_base + 1];
                    const float derived_width = scale_scheme == 1
                        ? initial_layer_width * static_cast<float>(1U << scale)
                        : initial_layer_width + step_increment * static_cast<float>(scale);
                    layer_params[scale].gaussian_width = configured_override > 0.0f
                        ? configured_override
                        : derived_width;
                }

                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                {
                    auto channel_pixels = read_channel(channel_index);

                    // Step 1: recursive Gaussian frequency analysis.
                    // Detail layers contain signed float differences; no premature clamping.
                    WaveletPyramid pyramid;
                    const auto on_layer_done = [&](int) {
                        report_progress(SSC_PROGRESS_STAGE_WAVELET_LAYER, ++completed_steps, total_steps);
                    };
                    const bool decomposed = decomposition_backend == 1
                        ? wavelet_decompose_atrous(
                            channel_pixels, width, height, 6, pyramid,
                            cancellation_flag, on_layer_done)
                        : wavelet_decompose_gaussian(
                            channel_pixels, width, height, layer_params, pyramid,
                            cancellation_flag, on_layer_done);
                    if (!decomposed)
                    {
                        return fail(SSC_STATUS_CANCELLED, "Wavelet processing was cancelled.");
                    }

                    // Step 2: Detail Layer Modulation (multiplier & soft-thresholding on float detail planes)
                    wavelet_modulate_details(pyramid, layer_params);

                    // Step 3: synthesis and reconstruction, without clipping.
                    std::vector<float> reconstructed;
                    wavelet_synthesize(pyramid, global_strength, solo, reconstructed);

                    write_channel(channel_index, reconstructed);
                }
                break;
            }
            case SSC_PROCESSOR_UNSHARP_MASK:
            {
                const float amount = parameters->values[0];
                const float radius = parameters->values[1];
                const float threshold = parameters->values[2];
                if (amount <= 1e-8f) break;
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                {
                    auto source = read_channel(channel_index);
                    std::vector<float> blurred;
                    if (!gaussian_blur(source, width, height, radius, blurred, cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Unsharp mask was cancelled.");
                    for (uint32_t y = 0; y < height; ++y)
                    {
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Unsharp mask was cancelled.");
                        for (uint32_t x = 0; x < width; ++x)
                        {
                            const size_t pixel = static_cast<size_t>(y) * width + x;
                            const float detail = soft_threshold(source[pixel] - blurred[pixel], threshold);
                            source[pixel] += amount * detail;
                        }
                    }
                    write_channel(channel_index, source);
                }
                break;
            }
            case SSC_PROCESSOR_MULTI_SCALE_SHARPEN:
            {
                const bool legacy = parameters->value_count == 4;
                const float fine_amount = legacy ? parameters->values[0] * 0.65f : parameters->values[0];
                const float fine_radius = parameters->values[1];
                const float broad_amount = legacy ? parameters->values[0] * 0.35f : parameters->values[2];
                const float broad_radius = legacy ? parameters->values[2] : parameters->values[3];
                const float threshold = legacy ? parameters->values[3] : parameters->values[4];
                if (fine_amount <= 1e-8f && broad_amount <= 1e-8f) break;

                // Broad Radius is an absolute target scale, not an additional blur applied
                // on top of Fine Radius. Keep the two bands ordered even for old/custom
                // presets that specify a broad radius below the fine radius.
                const float effective_broad_radius = std::max(broad_radius, fine_radius + 0.15f);
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                {
                    auto source = read_channel(channel_index);
                    std::vector<float> fine;
                    std::vector<float> broad;
                    if (!gaussian_blur(source, width, height, fine_radius, fine, cancellation_flag) ||
                        !gaussian_blur(source, width, height, effective_broad_radius, broad, cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Multi-scale sharpen was cancelled.");
                    for (uint32_t y = 0; y < height; ++y)
                    {
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Multi-scale sharpen was cancelled.");
                        for (uint32_t x = 0; x < width; ++x)
                        {
                            const size_t pixel = static_cast<size_t>(y) * width + x;
                            const float fine_detail = soft_threshold(source[pixel] - fine[pixel], threshold);
                            const float broad_detail = soft_threshold(fine[pixel] - broad[pixel], threshold);
                            source[pixel] += fine_amount * fine_detail + broad_amount * broad_detail;
                        }
                    }
                    write_channel(channel_index, source);
                }
                break;
            }
            case SSC_PROCESSOR_NOISE_REDUCTION:
            {
                const float luminance_strength = parameters->values[0];
                const float chrominance_strength = parameters->values[1];
                const float detail_protection = parameters->values[2];
                const float threshold_scale = parameters->values[3];
                if (luminance_strength <= 1e-8f && chrominance_strength <= 1e-8f) break;
                std::vector<std::vector<float>> source_channels;
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                    source_channels.push_back(read_channel(channel_index));

                if (channels == 1)
                {
                    std::vector<float> denoised;
                    if (!wavelet_shrink_channel(
                            source_channels[0], width, height,
                            luminance_strength, detail_protection, threshold_scale,
                            denoised, cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Noise reduction was cancelled.");
                    write_channel(0, denoised);
                }
                else
                {
                    std::vector<float> luminance(pixel_count);
                    std::vector<std::vector<float>> chroma(3, std::vector<float>(pixel_count));
                    for (uint32_t y = 0; y < height; ++y)
                    {
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Noise reduction was cancelled.");
                        for (uint32_t x = 0; x < width; ++x)
                        {
                            const size_t pixel = static_cast<size_t>(y) * width + x;
                            luminance[pixel] = 0.2126f * source_channels[0][pixel] +
                                0.7152f * source_channels[1][pixel] + 0.0722f * source_channels[2][pixel];
                            for (uint32_t channel_index = 0; channel_index < 3; ++channel_index)
                                chroma[channel_index][pixel] = source_channels[channel_index][pixel] - luminance[pixel];
                        }
                    }
                    std::vector<float> denoised_luminance;
                    if (!wavelet_shrink_channel(
                            luminance, width, height,
                            luminance_strength, detail_protection, threshold_scale,
                            denoised_luminance, cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Luminance noise reduction was cancelled.");
                    std::vector<std::vector<float>> denoised_chroma(3);
                    for (uint32_t channel_index = 0; channel_index < 3; ++channel_index)
                    {
                        if (!wavelet_shrink_channel(
                                chroma[channel_index], width, height,
                                chrominance_strength, detail_protection, threshold_scale,
                                denoised_chroma[channel_index], cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Chrominance noise reduction was cancelled.");
                    }

                    // Independent nonlinear shrinkage can leave a small luminance
                    // component in the three chroma planes. Remove it before RGB
                    // reconstruction so the luminance slider remains the sole owner
                    // of brightness-noise reduction.
                    for (uint32_t y = 0; y < height; ++y)
                    {
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Noise reduction was cancelled.");
                        for (uint32_t x = 0; x < width; ++x)
                        {
                            const size_t pixel = static_cast<size_t>(y) * width + x;
                            const float chroma_luminance =
                                0.2126f * denoised_chroma[0][pixel] +
                                0.7152f * denoised_chroma[1][pixel] +
                                0.0722f * denoised_chroma[2][pixel];
                            for (uint32_t channel_index = 0; channel_index < 3; ++channel_index)
                                storage->planar_pixels[static_cast<size_t>(channel_index) * pixel_count + pixel] =
                                    denoised_luminance[pixel] + denoised_chroma[channel_index][pixel] - chroma_luminance;
                        }
                    }
                }
                break;
            }
            case SSC_PROCESSOR_DERINGING:
            {
                const float strength = parameters->values[0];
                const float radius = std::clamp(parameters->values[1], 1.0f, 8.0f);
                const float edge_protection = parameters->values[2];
                if (strength <= 1e-8f) break;

                std::vector<std::vector<float>> source_channels;
                source_channels.reserve(channels);
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                    source_channels.push_back(read_channel(channel_index));

                std::vector<float> guide(pixel_count);
                if (channels == 3)
                {
                    for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                    {
                        if ((pixel & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Deringing was cancelled.");
                        guide[pixel] = 0.2126f * source_channels[0][pixel] +
                            0.7152f * source_channels[1][pixel] + 0.0722f * source_channels[2][pixel];
                    }
                }
                else
                {
                    guide = source_channels[0];
                }

                auto corrected_guide = guide;
                const int lower_radius = std::clamp(static_cast<int>(std::floor(radius)), 1, 8);
                const int upper_radius = std::clamp(static_cast<int>(std::ceil(radius)), 1, 8);
                const float radius_fraction = radius - static_cast<float>(lower_radius);
                const int lower_radius_squared = lower_radius * lower_radius;
                const int upper_radius_squared = upper_radius * upper_radius;

                for (uint32_t y = 0; y < height; ++y)
                {
                    if (is_cancelled(cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Deringing was cancelled.");
                    for (uint32_t x = 0; x < width; ++x)
                    {
                        float lower_minimum = std::numeric_limits<float>::infinity();
                        float lower_maximum = -std::numeric_limits<float>::infinity();
                        float upper_minimum = std::numeric_limits<float>::infinity();
                        float upper_maximum = -std::numeric_limits<float>::infinity();
                        for (int offset_y = -upper_radius; offset_y <= upper_radius; ++offset_y)
                        for (int offset_x = -upper_radius; offset_x <= upper_radius; ++offset_x)
                        {
                            if (offset_x == 0 && offset_y == 0) continue;
                            const int distance_squared = offset_x * offset_x + offset_y * offset_y;
                            if (distance_squared > upper_radius_squared) continue;
                            const int sample_x = reflected_index(static_cast<int>(x) + offset_x, static_cast<int>(width));
                            const int sample_y = reflected_index(static_cast<int>(y) + offset_y, static_cast<int>(height));
                            if (sample_x == static_cast<int>(x) && sample_y == static_cast<int>(y)) continue;
                            const float value = guide[static_cast<size_t>(sample_y) * width + sample_x];
                            upper_minimum = std::min(upper_minimum, value);
                            upper_maximum = std::max(upper_maximum, value);
                            if (distance_squared <= lower_radius_squared)
                            {
                                lower_minimum = std::min(lower_minimum, value);
                                lower_maximum = std::max(lower_maximum, value);
                            }
                        }
                        const size_t pixel = static_cast<size_t>(y) * width + x;
                        if (!std::isfinite(lower_minimum) || !std::isfinite(lower_maximum) ||
                            !std::isfinite(upper_minimum) || !std::isfinite(upper_maximum))
                        {
                            corrected_guide[pixel] = guide[pixel];
                            continue;
                        }

                        const float lower_edge = lower_maximum - lower_minimum;
                        const float upper_edge = upper_maximum - upper_minimum;
                        const float lower_extension = edge_protection * lower_edge;
                        const float upper_extension = edge_protection * upper_edge;
                        const float lower_corrected = std::clamp(
                            guide[pixel], lower_minimum - lower_extension, lower_maximum + lower_extension);
                        const float upper_corrected = std::clamp(
                            guide[pixel], upper_minimum - upper_extension, upper_maximum + upper_extension);
                        const float envelope_corrected =
                            lower_corrected + (upper_corrected - lower_corrected) * radius_fraction;
                        corrected_guide[pixel] =
                            guide[pixel] + (envelope_corrected - guide[pixel]) * strength;
                    }
                }

                if (channels == 3)
                {
                    // Apply one luminance-derived correction to all channels. This
                    // preserves the original chroma differences and avoids colored
                    // halos caused by independent per-channel extrema decisions.
                    for (uint32_t channel_index = 0; channel_index < 3; ++channel_index)
                    {
                        auto result = source_channels[channel_index];
                        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                        {
                            if ((pixel & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                                return fail(SSC_STATUS_CANCELLED, "Deringing was cancelled.");
                            result[pixel] += corrected_guide[pixel] - guide[pixel];
                        }
                        write_channel(channel_index, result);
                    }
                }
                else
                {
                    write_channel(0, corrected_guide);
                }
                break;
            }
            case SSC_PROCESSOR_RICHARDSON_LUCY:
            {
                const int iterations = std::clamp(static_cast<int>(std::lround(parameters->values[0])), 1, 50);
                const float radius = parameters->values[1];
                const float strength = parameters->values[2];
                const float damping = std::clamp(parameters->values[3], 0.0f, 1.0f);
                if (strength <= 1e-8f) break;
                constexpr float epsilon = 1e-6f;
                const uint32_t total_steps = channels * static_cast<uint32_t>(iterations);
                uint32_t completed_steps = 0;
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                {
                    auto source = read_channel(channel_index);
                    auto estimate = source;
                    for (float& value : estimate) value = std::max(0.0f, value);
                    for (int iteration = 0; iteration < iterations; ++iteration)
                    {
                        if (is_cancelled(cancellation_flag)) return fail(SSC_STATUS_CANCELLED, "Richardson-Lucy was cancelled between iterations.");
                        std::vector<float> blurred;
                        if (!gaussian_blur(estimate, width, height, radius, blurred, cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Richardson-Lucy was cancelled.");
                        std::vector<float> ratio(pixel_count);
                        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                        {
                            if ((pixel & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                                return fail(SSC_STATUS_CANCELLED, "Richardson-Lucy was cancelled.");
                            ratio[pixel] = std::max(0.0f, source[pixel]) / (blurred[pixel] + epsilon);
                        }
                        std::vector<float> correction;
                        if (!gaussian_blur(ratio, width, height, radius, correction, cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Richardson-Lucy was cancelled.");
                        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                        {
                            if ((pixel & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                                return fail(SSC_STATUS_CANCELLED, "Richardson-Lucy was cancelled.");
                            const float candidate = std::max(0.0f, estimate[pixel] * correction[pixel]);
                            estimate[pixel] += (candidate - estimate[pixel]) * (1.0f - damping);
                        }
                        report_progress(
                            SSC_PROGRESS_STAGE_RICHARDSON_LUCY_ITERATION,
                            ++completed_steps,
                            total_steps);
                    }
                    for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                        estimate[pixel] = source[pixel] + (estimate[pixel] - source[pixel]) * strength;
                    write_channel(channel_index, estimate);
                }
                break;
            }
            case SSC_PROCESSOR_RGB_ALIGN:
            {
                if (channels == 3)
                {
                    const bool automatic = parameters->values[0] >= 0.5f;
                    const int search = std::clamp(static_cast<int>(std::lround(parameters->values[7])), 1, 12);
                    std::vector<std::vector<float>> source_channels{read_channel(0), read_channel(1), read_channel(2)};
                    std::pair<float, float> auto_red{0.0f, 0.0f};
                    std::pair<float, float> auto_blue{0.0f, 0.0f};
                    if (automatic)
                    {
                        auto_red = phase_correlation_shift(source_channels[1], source_channels[0], width, height, search, cancellation_flag);
                        auto_blue = phase_correlation_shift(source_channels[1], source_channels[2], width, height, search, cancellation_flag);
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "RGB phase correlation was cancelled.");
                    }
                    const float shifts_x[3] = {
                        parameters->values[1] + auto_red.first,
                        parameters->values[3],
                        parameters->values[5] + auto_blue.first};
                    const float shifts_y[3] = {
                        parameters->values[2] + auto_red.second,
                        parameters->values[4],
                        parameters->values[6] + auto_blue.second};
                    for (uint32_t channel_index = 0; channel_index < 3; ++channel_index)
                    {
                        std::vector<float> aligned(pixel_count);
                        for (uint32_t y = 0; y < height; ++y)
                        {
                            if (is_cancelled(cancellation_flag))
                                return fail(SSC_STATUS_CANCELLED, "RGB alignment was cancelled.");
                            for (uint32_t x = 0; x < width; ++x)
                                aligned[static_cast<size_t>(y) * width + x] = lanczos3_sample(
                                    source_channels[channel_index], width, height,
                                    static_cast<float>(x) + shifts_x[channel_index],
                                    static_cast<float>(y) + shifts_y[channel_index]);
                        }
                        write_channel(channel_index, aligned);
                    }
                }
                break;
            }
            case SSC_PROCESSOR_ADVANCED_COLOR:
            {
                if (channels == 3)
                {
                    const float temperature = parameters->values[3];
                    const float tint = parameters->values[4];
                    const float gains[3] = {
                        parameters->values[0] * std::exp2(temperature * 0.25f - tint * 0.075f),
                        parameters->values[1] * std::exp2(tint * 0.15f),
                        parameters->values[2] * std::exp2(-temperature * 0.25f - tint * 0.075f)};
                    const float vibrance = parameters->values[5];
                    for (size_t pixel = 0; pixel < pixel_count; ++pixel)
                    {
                        if ((pixel & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Advanced color was cancelled.");
                        float red = storage->planar_pixels[pixel] * gains[0];
                        float green = storage->planar_pixels[pixel_count + pixel] * gains[1];
                        float blue = storage->planar_pixels[2 * pixel_count + pixel] * gains[2];
                        const float maximum = std::max({red, green, blue});
                        const float minimum = std::min({red, green, blue});
                        const float saturation = maximum <= 1e-8f ? 0.0f : (maximum - minimum) / maximum;
                        const float adaptive = 1.0f + vibrance * (1.0f - saturation);
                        const float luminance = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
                        storage->planar_pixels[pixel] = luminance + (red - luminance) * adaptive;
                        storage->planar_pixels[pixel_count + pixel] = luminance + (green - luminance) * adaptive;
                        storage->planar_pixels[2 * pixel_count + pixel] = luminance + (blue - luminance) * adaptive;
                    }
                }
                break;
            }
            case SSC_PROCESSOR_ADVANCED_TONE:
            {
                const float brightness = parameters->values[0];
                const float black = parameters->values[1];
                const float white = parameters->values[2];
                const float highlights = parameters->values[3];
                const float shadows = parameters->values[4];
                if (white <= black + 1e-6f) return fail(SSC_STATUS_INVALID_ARGUMENT, "White point must exceed black point.");
                for (size_t index = 0; index < storage->planar_pixels.size(); ++index)
                {
                    if ((index & 0xffffU) == 0 && is_cancelled(cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Advanced tone was cancelled.");
                    float& value = storage->planar_pixels[index];
                    float normalized = (value - black) / (white - black) + brightness;
                    const float bounded_position = std::clamp(normalized, 0.0f, 1.0f);
                    const float shadow_weight = (1.0f - bounded_position) * (1.0f - bounded_position);
                    const float highlight_weight = bounded_position * bounded_position;
                    normalized += shadows * shadow_weight * 0.25f + highlights * highlight_weight * 0.25f;
                    value = normalized;
                }
                break;
            }
            case SSC_PROCESSOR_LOCAL_DETAIL:
            {
                const float local_amount = parameters->values[0];
                const float local_radius = parameters->values[1];
                const float micro_amount = parameters->values[2];
                const float micro_radius = parameters->values[3];
                const float edge_protection = parameters->values[4];
                if (local_amount <= 1e-8f && micro_amount <= 1e-8f) break;
                for (uint32_t channel_index = 0; channel_index < channels; ++channel_index)
                {
                    auto source = read_channel(channel_index);
                    std::vector<float> local_blur;
                    std::vector<float> micro_blur;
                    if (!gaussian_blur(source, width, height, local_radius, local_blur, cancellation_flag) ||
                        !gaussian_blur(source, width, height, micro_radius, micro_blur, cancellation_flag))
                        return fail(SSC_STATUS_CANCELLED, "Local detail was cancelled.");
                    for (uint32_t y = 0; y < height; ++y)
                    {
                        if (is_cancelled(cancellation_flag))
                            return fail(SSC_STATUS_CANCELLED, "Local detail was cancelled.");
                        for (uint32_t x = 0; x < width; ++x)
                        {
                            const size_t pixel = static_cast<size_t>(y) * width + x;
                            const float local_detail = source[pixel] - local_blur[pixel];
                            const float micro_detail = source[pixel] - micro_blur[pixel];
                            const float protected_edge = std::max(std::abs(local_detail), std::abs(micro_detail));
                            const float gate = 1.0f / (1.0f + edge_protection * protected_edge * 20.0f);
                            source[pixel] += gate * (local_amount * local_detail + micro_amount * micro_detail);
                        }
                    }
                    write_channel(channel_index, source);
                }
                break;
            }
            }
        }

        auto image = std::make_unique<SSC_Image>();
        image->storage = std::move(storage);
        image->instance_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        image->is_master = false;
        *out_image = image.release();
        report_progress(SSC_PROGRESS_STAGE_PROCESSOR, 1, 1);
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate expert processor output.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected expert processor failure.");
    }
}

extern "C" SSC_Status ssc_image_compute_histogram(
    SSC_ImageHandle image,
    uint64_t* bins_red_or_gray,
    uint64_t* bins_green,
    uint64_t* bins_blue,
    uint32_t bin_count,
    SSC_HistogramStats* out_stats,
    const volatile uint32_t* cancellation_flag) noexcept
{
    clear_error();
    if (image == nullptr || bins_red_or_gray == nullptr || out_stats == nullptr || bin_count < 2)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Histogram arguments are invalid.");
    }
    if (out_stats->struct_size < sizeof(SSC_HistogramStats) ||
        out_stats->abi_version != STARSIM_CORE_NATIVE_ABI_VERSION)
    {
        return fail(SSC_STATUS_ABI_MISMATCH, "Histogram stats ABI layout is not supported.");
    }
    const uint32_t channels = image->storage->descriptor.channel_count;
    if (channels == 3 && (bins_green == nullptr || bins_blue == nullptr))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "RGB histogram requires three bin arrays.");
    }

    std::fill_n(bins_red_or_gray, bin_count, uint64_t{0});
    if (bins_green != nullptr) std::fill_n(bins_green, bin_count, uint64_t{0});
    if (bins_blue != nullptr) std::fill_n(bins_blue, bin_count, uint64_t{0});

    *out_stats = SSC_HistogramStats{};
    out_stats->struct_size = sizeof(SSC_HistogramStats);
    out_stats->abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
    out_stats->channel_count = channels;
    out_stats->bin_count = bin_count;
    const size_t pixel_count =
        static_cast<size_t>(image->storage->descriptor.width) * image->storage->descriptor.height;
    uint64_t* bins[3] = {bins_red_or_gray, bins_green, bins_blue};
    for (uint32_t channel = 0; channel < channels; ++channel)
    {
        float minimum = std::numeric_limits<float>::infinity();
        float maximum = -std::numeric_limits<float>::infinity();
        double sum = 0.0;
        for (size_t pixel = 0; pixel < pixel_count; ++pixel)
        {
            if ((pixel & 0xFFFFU) == 0 && cancellation_flag != nullptr && *cancellation_flag != 0)
            {
                return fail(SSC_STATUS_CANCELLED, "Histogram computation was cancelled.");
            }
            const float value = image->storage->planar_pixels[static_cast<size_t>(channel) * pixel_count + pixel];
            minimum = std::min(minimum, value);
            maximum = std::max(maximum, value);
            sum += value;
            const float clamped = std::clamp(value, 0.0f, 1.0f);
            const uint32_t bin = std::min(
                bin_count - 1,
                static_cast<uint32_t>(clamped * static_cast<float>(bin_count)));
            ++bins[channel][bin];
        }
        out_stats->minimum[channel] = minimum;
        out_stats->maximum[channel] = maximum;
        out_stats->mean[channel] = sum / static_cast<double>(pixel_count);
    }
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_get_pixel_f32(
    SSC_ImageHandle image,
    uint32_t x,
    uint32_t y,
    float* out_channels,
    uint32_t channel_capacity) noexcept
{
    clear_error();
    if (image == nullptr || out_channels == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image or pixel output is null.");
    }
    const auto& descriptor = image->storage->descriptor;
    if (x >= descriptor.width || y >= descriptor.height || channel_capacity < descriptor.channel_count)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Pixel coordinate or channel capacity is invalid.");
    }
    const size_t pixel_count = static_cast<size_t>(descriptor.width) * descriptor.height;
    const size_t pixel = static_cast<size_t>(y) * descriptor.width + x;
    for (uint32_t channel = 0; channel < descriptor.channel_count; ++channel)
    {
        out_channels[channel] = image->storage->planar_pixels[static_cast<size_t>(channel) * pixel_count + pixel];
    }
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_image_get_plane_pointers(
    SSC_ImageHandle image,
    const float** out_planes,
    uint32_t plane_capacity) noexcept
{
    clear_error();
    if (image == nullptr || out_planes == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image handle or plane pointers output array is null.");
    }
    const auto channel_count = image->storage->descriptor.channel_count;
    if (plane_capacity < channel_count)
    {
        return fail(SSC_STATUS_BUFFER_TOO_SMALL, "Plane capacity too small for channel count.");
    }
    const size_t pixel_count = static_cast<size_t>(image->storage->descriptor.width) * image->storage->descriptor.height;
    for (uint32_t c = 0; c < channel_count; ++c)
    {
        out_planes[c] = image->storage->planar_pixels.data() + static_cast<size_t>(c) * pixel_count;
    }
    return SSC_STATUS_OK;
}

extern "C" SSC_Status ssc_pipeline_create(
    SSC_ImageHandle immutable_master,
    SSC_PipelineHandle* out_pipeline) noexcept
{
    clear_error();
    if (immutable_master == nullptr || out_pipeline == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Master image or output pipeline pointer is null.");
    }
    *out_pipeline = nullptr;

    if (!immutable_master->is_master)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "A pipeline must be created from an immutable master image.");
    }

    try
    {
        auto working = std::make_shared<ImageStorage>(*immutable_master->storage);
        auto pipeline = std::make_unique<SSC_Pipeline>();
        pipeline->immutable_master = immutable_master->storage;
        pipeline->working_output = std::move(working);
        pipeline->output_identity = next_identity.fetch_add(1, std::memory_order_relaxed);
        *out_pipeline = pipeline.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate native pipeline storage.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected failure while creating native pipeline.");
    }
}

extern "C" SSC_Status ssc_pipeline_get_output_snapshot(
    SSC_PipelineHandle pipeline,
    SSC_ImageHandle* out_image) noexcept
{
    clear_error();
    if (pipeline == nullptr || out_image == nullptr)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Pipeline or output image pointer is null.");
    }
    *out_image = nullptr;

    try
    {
        auto image = std::make_unique<SSC_Image>();
        image->storage = pipeline->working_output;
        image->instance_identity = pipeline->output_identity;
        image->is_master = false;
        *out_image = image.release();
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate pipeline output handle.");
    }
    catch (...)
    {
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected failure while obtaining pipeline output.");
    }
}

extern "C" void ssc_pipeline_release(SSC_PipelineHandle pipeline) noexcept
{
    delete pipeline;
}

#if defined(_WIN32)
static FILE* open_write_binary(const std::wstring& path) noexcept
{
    FILE* file = nullptr;
    return _wfopen_s(&file, path.c_str(), L"wb") == 0 ? file : nullptr;
}
#else
static FILE* open_write_binary(const std::string& path) noexcept
{
    return std::fopen(path.c_str(), "wb");
}
#endif

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4611)
#endif
extern "C" SSC_Status ssc_image_export_file_utf8(
    SSC_ImageHandle image,
    const char* destination_path_utf8,
    SSC_ExportFormat format,
    const volatile uint32_t* cancellation_flag,
    SSC_ProgressCallback progress_callback,
    void* progress_context) noexcept
{
    clear_error();
    if (image == nullptr || destination_path_utf8 == nullptr || *destination_path_utf8 == '\0')
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Image or destination path is null/empty.");
    }
    if (format != SSC_EXPORT_FORMAT_TIFF_16 &&
        format != SSC_EXPORT_FORMAT_PNG_16 &&
        format != SSC_EXPORT_FORMAT_PNG_8 &&
        format != SSC_EXPORT_FORMAT_TIFF_FLOAT_32)
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Export format is not supported.");
    }

    const auto& descriptor = image->storage->descriptor;
    const uint32_t width = descriptor.width;
    const uint32_t height = descriptor.height;
    const uint32_t channels = descriptor.channel_count;
    const size_t pixel_count = static_cast<size_t>(width) * height;

    auto report_progress = [&](uint32_t current, uint32_t total)
    {
        if (progress_callback == nullptr) return;
        SSC_ProgressInfo info{};
        info.struct_size = sizeof(SSC_ProgressInfo);
        info.abi_version = STARSIM_CORE_NATIVE_ABI_VERSION;
        info.stage = SSC_PROGRESS_STAGE_PROCESSOR;
        info.current_step = current;
        info.total_steps = total;
        info.fraction = total > 0 ? static_cast<float>(current) / static_cast<float>(total) : 0.0f;
        progress_callback(&info, progress_context);
    };

#if defined(_WIN32)
    std::wstring wide_path;
    if (!utf8_to_wide(destination_path_utf8, wide_path))
    {
        return fail(SSC_STATUS_INVALID_ARGUMENT, "Destination path contains invalid UTF-8.");
    }
    std::wstring temp_path = wide_path + L".starsim_tmp";
#else
    std::string temp_path = std::string(destination_path_utf8) + ".starsim_tmp";
#endif

    try
    {
        if (format == SSC_EXPORT_FORMAT_TIFF_16 || format == SSC_EXPORT_FORMAT_TIFF_FLOAT_32)
        {
            const bool is_float = format == SSC_EXPORT_FORMAT_TIFF_FLOAT_32;
#if defined(_WIN32)
            TIFF* tiff = TIFFOpenW(temp_path.c_str(), "w");
#else
            TIFF* tiff = TIFFOpen(temp_path.c_str(), "w");
#endif
            if (tiff == nullptr)
            {
                return fail(SSC_STATUS_IO_ERROR, "Unable to create TIFF output file.");
            }
            TIFFSetField(tiff, TIFFTAG_IMAGEWIDTH, width);
            TIFFSetField(tiff, TIFFTAG_IMAGELENGTH, height);
            TIFFSetField(tiff, TIFFTAG_SAMPLESPERPIXEL, channels);
            TIFFSetField(tiff, TIFFTAG_BITSPERSAMPLE, is_float ? 32 : 16);
            TIFFSetField(tiff, TIFFTAG_SAMPLEFORMAT, is_float ? SAMPLEFORMAT_IEEEFP : SAMPLEFORMAT_UINT);
            TIFFSetField(tiff, TIFFTAG_ORIENTATION, ORIENTATION_TOPLEFT);
            TIFFSetField(tiff, TIFFTAG_PLANARCONFIG, PLANARCONFIG_CONTIG);
            TIFFSetField(tiff, TIFFTAG_PHOTOMETRIC,
                channels == 1 ? PHOTOMETRIC_MINISBLACK : PHOTOMETRIC_RGB);
            TIFFSetField(tiff, TIFFTAG_COMPRESSION, COMPRESSION_NONE);
            TIFFSetField(tiff, TIFFTAG_ROWSPERSTRIP, 1);

            std::vector<uint16_t> integer_row;
            std::vector<float> float_row;
            if (is_float) float_row.resize(static_cast<size_t>(width) * channels);
            else integer_row.resize(static_cast<size_t>(width) * channels);
            for (uint32_t y = 0; y < height; ++y)
            {
                if (is_cancelled(cancellation_flag))
                {
                    TIFFClose(tiff);
#if defined(_WIN32)
                    _wremove(temp_path.c_str());
#else
                    std::remove(temp_path.c_str());
#endif
                    return fail(SSC_STATUS_CANCELLED, "TIFF export was cancelled.");
                }
                for (uint32_t x = 0; x < width; ++x)
                {
                    for (uint32_t channel = 0; channel < channels; ++channel)
                    {
                        const float value = image->storage->planar_pixels[
                            static_cast<size_t>(channel) * pixel_count +
                            static_cast<size_t>(y) * width + x];
                        const size_t row_index = static_cast<size_t>(x) * channels + channel;
                        if (is_float)
                        {
                            float_row[row_index] = value;
                        }
                        else
                        {
                            const float clamped = std::clamp(value, 0.0f, 1.0f);
                            integer_row[row_index] = static_cast<uint16_t>(clamped * 65535.0f + 0.5f);
                        }
                    }
                }
                void* row_data = is_float
                    ? static_cast<void*>(float_row.data())
                    : static_cast<void*>(integer_row.data());
                if (TIFFWriteScanline(tiff, row_data, y) < 0)
                {
                    TIFFClose(tiff);
#if defined(_WIN32)
                    _wremove(temp_path.c_str());
#else
                    std::remove(temp_path.c_str());
#endif
                    return fail(SSC_STATUS_IO_ERROR, "TIFF write scanline failed.");
                }
                report_progress(y + 1, height);
            }
            TIFFClose(tiff);
        }
        else
        {
            // PNG export (16-bit or 8-bit)
            const bool is_16bit = (format == SSC_EXPORT_FORMAT_PNG_16);
#if defined(_WIN32)
            FILE* file = open_write_binary(temp_path);
#else
            FILE* file = open_write_binary(temp_path);
#endif
            if (file == nullptr)
            {
                return fail(SSC_STATUS_IO_ERROR, "Unable to create PNG output file.");
            }
            png_structp png = png_create_write_struct(
                PNG_LIBPNG_VER_STRING, nullptr, nullptr, nullptr);
            if (png == nullptr)
            {
                std::fclose(file);
#if defined(_WIN32)
                _wremove(temp_path.c_str());
#else
                std::remove(temp_path.c_str());
#endif
                return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate PNG writer.");
            }
            png_infop info = png_create_info_struct(png);
            if (info == nullptr)
            {
                png_destroy_write_struct(&png, nullptr);
                std::fclose(file);
#if defined(_WIN32)
                _wremove(temp_path.c_str());
#else
                std::remove(temp_path.c_str());
#endif
                return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate PNG info.");
            }
            if (setjmp(png_jmpbuf(png)) != 0)
            {
                png_destroy_write_struct(&png, &info);
                std::fclose(file);
#if defined(_WIN32)
                _wremove(temp_path.c_str());
#else
                std::remove(temp_path.c_str());
#endif
                return fail(SSC_STATUS_IO_ERROR, "PNG write error.");
            }
            png_init_io(png, file);
            const int color_type = channels == 1 ? PNG_COLOR_TYPE_GRAY : PNG_COLOR_TYPE_RGB;
            const int bit_depth = is_16bit ? 16 : 8;
            png_set_IHDR(png, info, width, height, bit_depth,
                color_type, PNG_INTERLACE_NONE,
                PNG_COMPRESSION_TYPE_DEFAULT, PNG_FILTER_TYPE_DEFAULT);
            png_write_info(png, info);
            if (is_16bit)
                png_set_swap(png); // Network byte order

            const size_t bytes_per_sample = is_16bit ? 2 : 1;
            const size_t row_size = static_cast<size_t>(width) * channels * bytes_per_sample;
            std::vector<uint8_t> row_buffer(row_size);
            for (uint32_t y = 0; y < height; ++y)
            {
                if (is_cancelled(cancellation_flag))
                {
                    png_destroy_write_struct(&png, &info);
                    std::fclose(file);
#if defined(_WIN32)
                    _wremove(temp_path.c_str());
#else
                    std::remove(temp_path.c_str());
#endif
                    return fail(SSC_STATUS_CANCELLED, "PNG export was cancelled.");
                }
                for (uint32_t x = 0; x < width; ++x)
                {
                    for (uint32_t channel = 0; channel < channels; ++channel)
                    {
                        const float value = image->storage->planar_pixels[
                            static_cast<size_t>(channel) * pixel_count +
                            static_cast<size_t>(y) * width + x];
                        const float clamped = std::clamp(value, 0.0f, 1.0f);
                        const size_t offset =
                            (static_cast<size_t>(x) * channels + channel) * bytes_per_sample;
                        if (is_16bit)
                        {
                            const uint16_t sample =
                                static_cast<uint16_t>(clamped * 65535.0f + 0.5f);
                            std::memcpy(row_buffer.data() + offset, &sample, sizeof(sample));
                        }
                        else
                        {
                            row_buffer[offset] =
                                static_cast<uint8_t>(clamped * 255.0f + 0.5f);
                        }
                    }
                }
                png_write_row(png, row_buffer.data());
                report_progress(y + 1, height);
            }
            png_write_end(png, info);
            png_destroy_write_struct(&png, &info);
            std::fclose(file);
        }

        // Safe finalization: rename temp to destination
#if defined(_WIN32)
        if (MoveFileExW(temp_path.c_str(), wide_path.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) == 0)
        {
            _wremove(temp_path.c_str());
            return fail(SSC_STATUS_IO_ERROR, "Unable to finalize exported file.");
        }
#else
        if (std::rename(temp_path.c_str(), destination_path_utf8) != 0)
        {
            std::remove(temp_path.c_str());
            return fail(SSC_STATUS_IO_ERROR, "Unable to finalize exported file.");
        }
#endif
        return SSC_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
#if defined(_WIN32)
        _wremove(temp_path.c_str());
#else
        std::remove(temp_path.c_str());
#endif
        return fail(SSC_STATUS_OUT_OF_MEMORY, "Unable to allocate export buffer.");
    }
    catch (...)
    {
#if defined(_WIN32)
        _wremove(temp_path.c_str());
#else
        std::remove(temp_path.c_str());
#endif
        return fail(SSC_STATUS_INTERNAL_ERROR, "Unexpected export failure.");
    }
}
#if defined(_MSC_VER)
#pragma warning(pop)
#endif
