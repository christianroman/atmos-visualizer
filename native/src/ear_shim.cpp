// ear_shim — C API over libear. See include/ear_shim.h for conventions.
//
// Every exported function catches all C++ exceptions: an exception must never cross into
// C#, where it would crash the process instead of surfacing as an error.

#include "ear_shim.h"

#include <cstring>
#include <exception>
#include <string>
#include <vector>

#include "allocentric.hpp"

#include <ear/bs2051.hpp>
#include <ear/conversion.hpp>
#include <ear/ear.hpp>
#include <ear/gain_calculators.hpp>
#include <ear/layout.hpp>
#include <ear/metadata.hpp>

#ifndef LIBEAR_COMMIT
#define LIBEAR_COMMIT "unknown"
#endif

namespace {

thread_local std::string g_lastError;

int fail(const std::string& message) {
  g_lastError = message;
  return -1;
}

template <typename F>
int guarded(F&& f) {
  try {
    g_lastError.clear();
    return f();
  } catch (const std::exception& e) {
    return fail(e.what());
  } catch (...) {
    return fail("unknown C++ exception");
  }
}

template <typename F>
void* guardedNew(F&& f) {
  try {
    g_lastError.clear();
    return f();
  } catch (const std::exception& e) {
    fail(e.what());
  } catch (...) {
    fail("unknown C++ exception");
  }
  return nullptr;
}

struct LayoutHandle {
  ear::Layout layout;
};

struct ObjectsHandle {
  explicit ObjectsHandle(const ear::Layout& l)
      : calc(l),
        channelCount(static_cast<int>(l.channels().size())),
        direct(channelCount),
        diffuse(channelCount) {}
  ear::GainCalculatorObjects calc;
  int channelCount;
  std::vector<double> direct;
  std::vector<double> diffuse;
};

struct DirectHandle {
  explicit DirectHandle(const ear::Layout& l)
      : calc(l), channelCount(static_cast<int>(l.channels().size())), gains(channelCount) {}
  ear::GainCalculatorDirectSpeakers calc;
  int channelCount;
  std::vector<double> gains;
};

struct AlloHandle {
  AlloHandle(std::vector<allo::Pos> positions, std::vector<bool> lfe)
      : calc(std::move(positions)), isLfe(std::move(lfe)) {}
  allo::GainCalculator calc;
  std::vector<bool> isLfe;
  std::vector<double> tmp;
};

const ear::Layout& layoutOf(const void* layout) {
  if (!layout) throw std::invalid_argument("layout handle is null");
  return static_cast<const LayoutHandle*>(layout)->layout;
}

const ear::Channel& channelOf(const void* layout, int index) {
  const auto& channels = layoutOf(layout).channels();
  if (index < 0 || index >= static_cast<int>(channels.size()))
    throw std::out_of_range("channel index out of range");
  return channels[static_cast<size_t>(index)];
}

void warnToError(const ear::Warning& w) {
  // libear reports non-fatal issues through a callback. Keep the latest one so callers can
  // see it, without failing the call.
  g_lastError = "warning: " + w.message;
}

int calcObject(void* calc, ear::ObjectsTypeMetadata& md, double* outGains) {
  if (!calc) return fail("calculator handle is null");
  if (!outGains) return fail("outGains is null");
  auto* h = static_cast<ObjectsHandle*>(calc);
  h->calc.calculate(md, h->direct, h->diffuse, warnToError);
  for (int i = 0; i < h->channelCount; ++i) outGains[i] = h->direct[static_cast<size_t>(i)];
  return 0;
}

}  // namespace

extern "C" {

const char* es_version(void) { return "ear_shim 0.1.0, libear " LIBEAR_COMMIT; }

const char* es_last_error(void) { return g_lastError.c_str(); }

// ---- Layouts ----------------------------------------------------------------------------

void* es_layout_bs2051(const char* name) {
  return guardedNew([&]() -> void* {
    if (!name) throw std::invalid_argument("name is null");
    return new LayoutHandle{ear::getLayout(name)};
  });
}

void* es_layout_custom(const char* layoutName, int count, const char* const* names,
                       const double* azimuths, const double* elevations, const int* isLfe) {
  return guardedNew([&]() -> void* {
    if (count <= 0 || !names || !azimuths || !elevations || !isLfe)
      throw std::invalid_argument("bad custom layout arguments");
    std::vector<ear::Channel> channels;
    for (int i = 0; i < count; ++i) {
      ear::PolarPosition pos(azimuths[i], elevations[i], 1.0);
      channels.emplace_back(names[i], pos, boost::none, boost::none, boost::none,
                            isLfe[i] != 0);
    }
    return new LayoutHandle{ear::Layout(layoutName ? layoutName : "custom", channels)};
  });
}

void es_layout_free(void* layout) { delete static_cast<LayoutHandle*>(layout); }

int es_layout_channel_count(const void* layout) {
  return guarded([&]() { return static_cast<int>(layoutOf(layout).channels().size()); });
}

int es_layout_channel_name(const void* layout, int index, char* buf, int bufSize) {
  return guarded([&]() {
    const std::string& name = channelOf(layout, index).name();
    if (!buf || bufSize <= static_cast<int>(name.size())) return fail("buffer too small");
    std::memcpy(buf, name.c_str(), name.size() + 1);
    return 0;
  });
}

int es_layout_channel_polar(const void* layout, int index, double* azimuth, double* elevation,
                            double* distance) {
  return guarded([&]() {
    ear::PolarPosition p = channelOf(layout, index).polarPosition();
    if (azimuth) *azimuth = p.azimuth;
    if (elevation) *elevation = p.elevation;
    if (distance) *distance = p.distance;
    return 0;
  });
}

int es_layout_channel_is_lfe(const void* layout, int index) {
  return guarded([&]() { return channelOf(layout, index).isLfe() ? 1 : 0; });
}

// ---- Objects ----------------------------------------------------------------------------

void* es_objects_new(const void* layout) {
  return guardedNew([&]() -> void* { return new ObjectsHandle(layoutOf(layout)); });
}

void es_objects_free(void* calc) { delete static_cast<ObjectsHandle*>(calc); }

int es_objects_gains_polar(void* calc, double azimuth, double elevation, double distance,
                           double width, double height, double depth, double gain,
                           double* outGains) {
  return guarded([&]() {
    ear::ObjectsTypeMetadata md;
    md.position = ear::PolarPosition(azimuth, elevation, distance);
    md.width = width;
    md.height = height;
    md.depth = depth;
    md.gain = gain;
    md.cartesian = false;
    return calcObject(calc, md, outGains);
  });
}

int es_objects_gains_cart_via_polar(void* calc, double x, double y, double z, double width,
                                    double height, double depth, double gain,
                                    double* outGains) {
  return guarded([&]() {
    ear::ObjectsTypeMetadata md;
    md.position = ear::CartesianPosition(x, y, z);
    md.width = width;
    md.height = height;
    md.depth = depth;
    md.gain = gain;
    md.cartesian = true;
    ear::conversion::toPolar(md);  // BS.2127 section 10; clears the cartesian flag
    return calcObject(calc, md, outGains);
  });
}

// ---- Room-relative ("allocentric") objects ------------------------------------------------

void* es_allo_new(const void* layout, const double* positions) {
  return guardedNew([&]() -> void* {
    if (!positions) throw std::invalid_argument("positions is null");
    const auto& channels = layoutOf(layout).channels();
    std::vector<allo::Pos> pos;
    std::vector<bool> lfe;
    for (size_t i = 0; i < channels.size(); ++i) {
      lfe.push_back(channels[i].isLfe());
      if (!channels[i].isLfe())
        pos.push_back({positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]});
    }
    return new AlloHandle(std::move(pos), std::move(lfe));
  });
}

void es_allo_free(void* calc) { delete static_cast<AlloHandle*>(calc); }

int es_allo_gains(void* calc, double x, double y, double z, double width, double height,
                  double depth, double gain, double* outGains) {
  return guarded([&]() {
    if (!calc) return fail("calculator handle is null");
    if (!outGains) return fail("outGains is null");
    auto* h = static_cast<AlloHandle*>(calc);
    h->calc.gains({x, y, z}, width, height, depth, gain, h->tmp);
    for (size_t i = 0, k = 0; i < h->isLfe.size(); ++i)
      outGains[i] = h->isLfe[i] ? 0.0 : h->tmp[k++];
    return 0;
  });
}

// ---- Conversion -------------------------------------------------------------------------

int es_point_cart_to_polar(double x, double y, double z, double* azimuth, double* elevation,
                           double* distance) {
  return guarded([&]() {
    ear::PolarPosition p = ear::conversion::pointCartToPolar(ear::CartesianPosition(x, y, z));
    if (azimuth) *azimuth = p.azimuth;
    if (elevation) *elevation = p.elevation;
    if (distance) *distance = p.distance;
    return 0;
  });
}

int es_extent_cart_to_polar(double x, double y, double z, double width, double height,
                            double depth, double* azimuth, double* elevation,
                            double* distance, double* polarWidth, double* polarHeight,
                            double* polarDepth) {
  return guarded([&]() {
    auto r = ear::conversion::extentCartToPolar(ear::CartesianPosition(x, y, z),
                                                {width, height, depth});
    if (azimuth) *azimuth = r.first.azimuth;
    if (elevation) *elevation = r.first.elevation;
    if (distance) *distance = r.first.distance;
    if (polarWidth) *polarWidth = r.second.width;
    if (polarHeight) *polarHeight = r.second.height;
    if (polarDepth) *polarDepth = r.second.depth;
    return 0;
  });
}

// ---- DirectSpeakers ---------------------------------------------------------------------

void* es_direct_new(const void* layout) {
  return guardedNew([&]() -> void* { return new DirectHandle(layoutOf(layout)); });
}

void es_direct_free(void* calc) { delete static_cast<DirectHandle*>(calc); }

int es_direct_gains(void* calc, const char* speakerLabel, double azimuth, double elevation,
                    double distance, double lfeLowPassHz, double* outGains) {
  return guarded([&]() {
    if (!calc) return fail("calculator handle is null");
    if (!outGains) return fail("outGains is null");
    auto* h = static_cast<DirectHandle*>(calc);
    ear::DirectSpeakersTypeMetadata md;
    if (speakerLabel && *speakerLabel) md.speakerLabels = {speakerLabel};
    md.position = ear::PolarSpeakerPosition(azimuth, elevation, distance);
    if (lfeLowPassHz > 0) md.channelFrequency.lowPass = lfeLowPassHz;
    h->calc.calculate(md, h->gains, warnToError);
    for (int i = 0; i < h->channelCount; ++i) outGains[i] = h->gains[static_cast<size_t>(i)];
    return 0;
  });
}

}  // extern "C"
