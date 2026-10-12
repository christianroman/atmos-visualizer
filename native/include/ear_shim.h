/*
 * ear_shim — a small C API over libear (ITU-R BS.2127), for Unity P/Invoke.
 *
 * Conventions
 *  - Every function returning int returns 0 on success and a negative value on failure;
 *    es_last_error() then describes the failure (per thread).
 *  - Handles are opaque pointers (IntPtr in C#). Free each with its matching *_free.
 *  - Polar angles follow ADM/BS.2127: degrees, azimuth anticlockwise (positive = LEFT),
 *    elevation positive = up.
 *  - Cartesian positions follow ADM/DAMF: X -1 left .. +1 right, Y -1 rear .. +1 front,
 *    Z 0 ear level .. +1 ceiling (-1 floor).
 *  - Gain arrays have one entry per layout channel (es_layout_channel_count), in layout
 *    order, LFE channels included (objects always give 0 on LFE channels).
 *  - A gain calculator is not thread-safe; give each thread its own.
 */
#ifndef EAR_SHIM_H
#define EAR_SHIM_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#if defined(EAR_SHIM_BUILD)
#define ES_API __declspec(dllexport)
#else
#define ES_API __declspec(dllimport)
#endif
#else
#define ES_API __attribute__((visibility("default")))
#endif

/* Version of this shim and the libear commit it was built from. */
ES_API const char* es_version(void);

/* Message for the most recent failure on this thread ("" if none). */
ES_API const char* es_last_error(void);

/* ---- Layouts ---------------------------------------------------------------------- */

/* A BS.2051 layout by name, e.g. "0+2+0", "2+5+0", "4+7+0". NULL on failure. */
ES_API void* es_layout_bs2051(const char* name);

/* A custom layout. Channel names are free text, except that an LFE channel should be named
 * "LFE1" or "LFE2" so DirectSpeakers LFE routing finds it. Real and nominal positions are
 * the same. NULL on failure. */
ES_API void* es_layout_custom(const char* layoutName, int count, const char* const* names,
                              const double* azimuths, const double* elevations,
                              const int* isLfe);

ES_API void es_layout_free(void* layout);
ES_API int es_layout_channel_count(const void* layout);
/* Copies the channel name (UTF-8, NUL-terminated) into buf. */
ES_API int es_layout_channel_name(const void* layout, int index, char* buf, int bufSize);
/* Real (not nominal) polar position of a channel. */
ES_API int es_layout_channel_polar(const void* layout, int index, double* azimuth,
                                   double* elevation, double* distance);
/* 1 if the channel is an LFE channel, 0 if not, negative on error. */
ES_API int es_layout_channel_is_lfe(const void* layout, int index);

/* ---- Objects ---------------------------------------------------------------------- */

/* Object gain calculator for a layout. The layout may be freed afterwards. */
ES_API void* es_objects_new(const void* layout);
ES_API void es_objects_free(void* calc);

/* Gains for a polar object. width/height in degrees, depth in distance units, gain linear.
 * diffuse is assumed 0 (DAMF has no diffuseness), so these are the direct gains. */
ES_API int es_objects_gains_polar(void* calc, double azimuth, double elevation,
                                  double distance, double width, double height,
                                  double depth, double gain, double* outGains);

/* Gains for a Cartesian object, converted to polar with BS.2127 section 10
 * (libear conversion::toPolar on position and width/height/depth) and then panned with the
 * polar (egocentric) panner. NOTE: this is NOT BS.2127's rendering of cartesian=true
 * objects, which uses the allocentric panner; libear does not implement that path. */
ES_API int es_objects_gains_cart_via_polar(void* calc, double x, double y, double z,
                                           double width, double height, double depth,
                                           double gain, double* outGains);

/* ---- Room-relative ("allocentric") objects --------------------------------------------
 * BS.2127's rendering of Cartesian (cartesian=true) objects, which libear does not implement.
 * Ported from the EBU ADM Renderer reference (ear 2.1.0); see src/allocentric.hpp.
 * positions: 3 doubles (X, Y, Z) per layout channel - each speaker's position in the room box,
 * same axes as objects. Entries for LFE channels are ignored (LFE channels get gain 0).
 * width/height/depth are the ADM extent parameters (0 = point source), passed on exactly as the
 * reference does: it hands them to its extent panner as (x, y, z) sizes in the order
 * width, height, depth. DAMF has a single size, so all three are equal and the order is moot. */
ES_API void* es_allo_new(const void* layout, const double* positions);
ES_API void es_allo_free(void* calc);
ES_API int es_allo_gains(void* calc, double x, double y, double z, double width, double height,
                         double depth, double gain, double* outGains);

/* ---- Conversion (BS.2127 section 10) ---------------------------------------------- */

ES_API int es_point_cart_to_polar(double x, double y, double z, double* azimuth,
                                  double* elevation, double* distance);
ES_API int es_extent_cart_to_polar(double x, double y, double z, double width,
                                   double height, double depth, double* azimuth,
                                   double* elevation, double* distance,
                                   double* polarWidth, double* polarHeight,
                                   double* polarDepth);

/* ---- DirectSpeakers (bed channels, e.g. the LFE) --------------------------------- */

ES_API void* es_direct_new(const void* layout);
ES_API void es_direct_free(void* calc);

/* Gains for one bed channel given its speaker label (e.g. "LFE1", "M+030") and nominal
 * polar position. lfeLowPassHz > 0 adds a frequency element marking it as LFE content
 * (BS.2127 treats lowPass <= 200 Hz as LFE). */
ES_API int es_direct_gains(void* calc, const char* speakerLabel, double azimuth,
                           double elevation, double distance, double lfeLowPassHz,
                           double* outGains);

#ifdef __cplusplus
}
#endif

#endif /* EAR_SHIM_H */
