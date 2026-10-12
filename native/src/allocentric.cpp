/*
 * Portions of this file are a C++ port of the EBU ADM Renderer (ear 2.1.0), distributed under
 * the BSD-3-Clause-Clear license below.
 *
 * Copyright (c) 2018-2019 EBU ADM Renderer Authors
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification, are
 * permitted (subject to the limitations in the disclaimer below) provided that the following
 * conditions are met:
 *
 * * Redistributions of source code must retain the above copyright notice, this list of
 * conditions and the following disclaimer.
 *
 * * Redistributions in binary form must reproduce the above copyright notice, this list of
 * conditions and the following disclaimer in the documentation and/or other materials provided
 * with the distribution.
 *
 * * Neither the name of the copyright holder nor the names of its contributors may be used to
 * endorse or promote products derived from this software without specific prior written
 * permission.
 *
 * NO EXPRESS OR IMPLIED LICENSES TO ANY PARTY'S PATENT RIGHTS ARE GRANTED BY THIS LICENSE.
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS
 * OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
 * COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
 * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR
 * TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
 * EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Port of the EBU ADM Renderer's allocentric (room-relative) panning; see allocentric.hpp.
// Function names and structure follow the Python so the two can be read side by side.

#include "allocentric.hpp"

#include <algorithm>
#include <cmath>
#include <set>
#include <stdexcept>

namespace allo {

namespace {

const double kPi = 3.141592653589793;  // numpy.pi

/// AllocentricPanner._single_balance_pan
std::pair<double, double> singleBalancePan(double minimum, double maximum, double value) {
  if (minimum == maximum) return {1.0, 1.0};
  if (value <= minimum) return {0.0, 1.0};
  if (value >= maximum) return {1.0, 0.0};
  double a = (value - minimum) / (maximum - minimum);
  double aa = a * kPi / 2.0;
  return {std::cos(aa), std::sin(aa)};
}

}  // namespace

// ---- ear/core/point_source.py: AllocentricPanner ----------------------------------------

PointPanner::PointPanner(const std::vector<Pos>& positions)
    : n_(static_cast<int>(positions.size())) {
  if (positions.empty()) throw std::invalid_argument("allocentric panner needs at least one speaker");
  // _speaker_tree: planes sorted by z, rows by y, columns by x; exact float comparisons.
  for (int index = 0; index < n_; ++index) {
    const Pos& c = positions[static_cast<size_t>(index)];
    Entry entry{index, c};

    size_t zi = 0;
    bool found = false, inserted = false;
    for (; zi < tree_.size(); ++zi) {
      double zPos = tree_[zi][0][0].pos.z;
      if (zPos == c.z) { found = true; break; }
      if (zPos > c.z) { tree_.insert(tree_.begin() + static_cast<long>(zi), Plane{Row{entry}}); inserted = true; break; }
    }
    if (!found && !inserted) { tree_.push_back(Plane{Row{entry}}); inserted = true; }
    if (inserted) continue;

    Plane& plane = tree_[zi];
    size_t yi = 0;
    found = false;
    for (; yi < plane.size(); ++yi) {
      double yPos = plane[yi][0].pos.y;
      if (yPos == c.y) { found = true; break; }
      if (yPos > c.y) { plane.insert(plane.begin() + static_cast<long>(yi), Row{entry}); inserted = true; break; }
    }
    if (!found && !inserted) { plane.push_back(Row{entry}); inserted = true; }
    if (inserted) continue;

    Row& row = plane[yi];
    size_t xi = 0;
    for (; xi < row.size(); ++xi) {
      double xPos = row[xi].pos.x;
      if (xPos == c.x) throw std::invalid_argument("two speakers with the same allocentric position");
      if (xPos > c.x) break;
    }
    row.insert(row.begin() + static_cast<long>(xi), entry);
  }
}

std::pair<int, int> PointPanner::findPlanes(double z) const {
  if (z <= tree_[0][0][0].pos.z) return {0, 0};
  for (size_t i = 0; i < tree_.size(); ++i) {
    double zPos = tree_[i][0][0].pos.z;
    if (zPos == z) return {static_cast<int>(i), static_cast<int>(i)};
    if (zPos > z) return {static_cast<int>(i) - 1, static_cast<int>(i)};
  }
  int last = static_cast<int>(tree_.size()) - 1;
  return {last, last};
}

std::pair<int, int> PointPanner::findRows(const Plane& plane, double y) {
  if (y <= plane[0][0].pos.y) return {0, 0};
  for (size_t i = 0; i < plane.size(); ++i) {
    double yPos = plane[i][0].pos.y;
    if (yPos == y) return {static_cast<int>(i), static_cast<int>(i)};
    if (yPos > y) return {static_cast<int>(i) - 1, static_cast<int>(i)};
  }
  int last = static_cast<int>(plane.size()) - 1;
  return {last, last};
}

std::pair<int, int> PointPanner::findColumns(const Row& row, double x) {
  if (x <= row[0].pos.x) return {0, 0};
  for (size_t i = 0; i < row.size(); ++i) {
    double xPos = row[i].pos.x;
    if (xPos == x) return {static_cast<int>(i), static_cast<int>(i)};
    if (xPos > x) return {static_cast<int>(i) - 1, static_cast<int>(i)};
  }
  int last = static_cast<int>(row.size()) - 1;
  return {last, last};
}

void PointPanner::handle(const Pos& p, std::vector<double>& out) const {
  out.assign(static_cast<size_t>(n_), 0.0);
  auto zPlanes = findPlanes(p.z);
  int zp[2] = {zPlanes.first, zPlanes.second};
  auto zg = singleBalancePan(tree_[zp[0]][0][0].pos.z, tree_[zp[1]][0][0].pos.z, p.z);
  double zGains[2] = {zg.first, zg.second};
  for (int a = 0; a < 2; ++a) {
    const Plane& plane = tree_[zp[a]];
    auto yRows = findRows(plane, p.y);
    int yr[2] = {yRows.first, yRows.second};
    auto yg = singleBalancePan(plane[yr[0]][0].pos.y, plane[yr[1]][0].pos.y, p.y);
    double yGains[2] = {yg.first, yg.second};
    for (int b = 0; b < 2; ++b) {
      const Row& row = plane[yr[b]];
      auto xCols = findColumns(row, p.x);
      int xc[2] = {xCols.first, xCols.second};
      auto xg = singleBalancePan(row[xc[0]].pos.x, row[xc[1]].pos.x, p.x);
      double xGains[2] = {xg.first, xg.second};
      // assignment, not accumulation, exactly as the reference (ret[...] = ...)
      for (int c = 0; c < 2; ++c)
        out[static_cast<size_t>(row[xc[c]].index)] = zGains[a] * yGains[b] * xGains[c];
    }
  }
}

// ---- ear/core/objectbased/allo_extent.py --------------------------------------------------

namespace {

const int kNumVs = 40;
const double kNeg130dBExpLim = 6.5;
const double kNeg130dBLim = std::pow(10.0, -kNeg130dBExpLim);

std::vector<double> linspace(double start, double stop, int num) {
  // numpy.linspace: start + i * step, with the last value set exactly to stop
  std::vector<double> y(static_cast<size_t>(num));
  double step = (stop - start) / (num - 1);
  for (int i = 0; i < num; ++i) y[static_cast<size_t>(i)] = i * step + start;
  y[static_cast<size_t>(num - 1)] = stop;
  return y;
}

double interp(double x, const double* xp, const double* fp, int n) {
  // numpy.interp for increasing xp, clamping outside the range
  if (x <= xp[0]) return fp[0];
  if (x >= xp[n - 1]) return fp[n - 1];
  int i = 0;
  while (!(x < xp[i + 1])) ++i;
  double slope = (fp[i + 1] - fp[i]) / (xp[i + 1] - xp[i]);
  return slope * (x - xp[i]) + fp[i];
}

double scaleSize(double v) {
  static const double xp[] = {0.0, 0.2, 0.5, 0.75, 1.0};
  static const double fp[] = {0.0, 0.3, 1.0, 1.8, 2.8};
  return interp(std::min(v, 1.0), xp, fp, 5);
}

double sEff(const std::vector<Pos>& cp, double sx, double sy, double sz) {
  bool sameYZ = true, sameZ = true;
  for (const auto& c : cp) {
    if (c.y != cp[0].y || c.z != cp[0].z) sameYZ = false;
    if (c.z != cp[0].z) sameZ = false;
  }
  if (sameYZ) return sx;  // speakers in a left/right line
  if (sameZ) {            // speakers in a horizontal plane
    double hi = std::max(sx, sy), lo = std::min(sx, sy);
    return (3.0 / 4.0) * hi + (1.0 / 4.0) * lo;
  }
  double s[3] = {sx, sy, sz};  // speakers in a cube
  std::sort(s, s + 3);
  return (6.0 / 9.0) * s[2] + (2.0 / 9.0) * s[1] + (1.0 / 9.0) * s[0];
}

double pOf(double s_eff) {
  if (s_eff <= 0.5) return 6.0;
  const double s_max = 2.8;
  return 6.0 - 4.0 * ((s_eff - 0.5) / (s_max - 0.5));
}

double hOf(double s, double d_bound) {
  if (d_bound >= 2.0 * s && d_bound >= 0.4) {
    double n = std::max(2.0 * s, 0.4);
    double nnn = n * n * n;
    return std::pow(nnn / (0.16 * 2.0 * s), 1.0 / 3.0);
  }
  double a = d_bound / 0.4;
  double b = d_bound / 2.0 * (a * a);
  return std::pow(b, 1.0 / 3.0);
}

double dBound(int dim, double xo, double yo, double zo) {
  if (dim == 1) return std::min(xo + 1, 1 - xo);
  if (dim == 2) return std::min({xo + 1, 1 - xo, yo + 1, 1 - yo});
  return std::min({xo + 1, 1 - xo, yo + 1, 1 - yo, zo + 1, 1 - zo});
}

double muOf(int dim, double sx, double sy, double sz, double xo, double yo, double zo) {
  double d = dBound(dim, xo, yo, zo);
  if (dim == 1) {
    double n = hOf(sx, d);
    return n * n * n;
  }
  if (dim == 2) {
    double n = hOf(sx, d) * hOf(sy, d);
    return std::pow(n, 1.5);
  }
  return hOf(sx, d) * hOf(sy, d) * hOf(sz, d);
}

std::vector<double> weights(const std::vector<double>& vs, double o, double denom, bool zAxis) {
  std::vector<double> w(vs.size());
  for (size_t i = 0; i < vs.size(); ++i) {
    double t = -std::min(std::pow(1.5 * (vs[i] - o) / denom, 4.0), kNeg130dBExpLim);
    w[i] = std::pow(10.0, t);
    if (zAxis) w[i] *= std::cos(vs[i] * kPi * (3.0 / 7.0));
  }
  return w;
}

int dimOf(const std::vector<Pos>& cp) {
  bool dx = false, dy = false, dz = false;
  for (const auto& c : cp) {
    dx |= c.x != cp[0].x;
    dy |= c.y != cp[0].y;
    dz |= c.z != cp[0].z;
  }
  return int(dx) + int(dy) + int(dz);
}

// Bounds search shared by _find_plane_z / _find_row_y / _find_column_x: the nearest speaker
// coordinate at or below v (lo) and at or above v (hi), among speakers passing `member`.
template <typename Coord, typename Member>
void findBounds(double v, const std::vector<Pos>& cp, Coord coord, Member member, bool& haveLo,
                double& lo, bool& haveHi, double& hi) {
  haveLo = haveHi = false;
  lo = hi = 0;
  for (const auto& s : cp) {
    if (!member(s)) continue;
    double c = coord(s);
    if (c <= v) { lo = haveLo ? std::max(lo, c) : c; haveLo = true; }
    if (c >= v) { hi = haveHi ? std::min(hi, c) : c; haveHi = true; }
  }
}

double pointGain1D(double pos, double v, bool haveLo, double lo, bool haveHi, double hi) {
  if (!haveLo) return pos != hi ? 0.0 : 1.0;
  if (!haveHi) return pos != lo ? 0.0 : 1.0;
  if (lo <= pos && pos <= hi) {
    if (lo == hi) return 1.0;
    if (lo == pos) return std::cos((v - lo) / (hi - lo) * kPi / 2.0);
    return std::sin((v - lo) / (hi - lo) * kPi / 2.0);
  }
  return 0.0;
}

// _calc_g_point_separated: per speaker, per virtual-source coordinate, gain along each axis.
// Result layout: [speaker][i].
using Table = std::vector<std::vector<double>>;

void gPointSeparated(const std::vector<Pos>& cp, const std::vector<double>& xs,
                     const std::vector<double>& ys, const std::vector<double>& zs, Table& gx,
                     Table& gy, Table& gz) {
  size_t n = cp.size();
  gx.assign(n, {});
  gy.assign(n, {});
  gz.assign(n, {});
  bool haveLo, haveHi;
  double lo, hi;
  for (size_t k = 0; k < n; ++k) {
    const Pos& s = cp[k];
    for (double z : zs) {
      findBounds(z, cp, [](const Pos& p) { return p.z; }, [](const Pos&) { return true; },
                 haveLo, lo, haveHi, hi);
      gz[k].push_back(pointGain1D(s.z, z, haveLo, lo, haveHi, hi));
    }
    for (double y : ys) {
      findBounds(y, cp, [](const Pos& p) { return p.y; }, [&](const Pos& p) { return p.z == s.z; },
                 haveLo, lo, haveHi, hi);
      gy[k].push_back(pointGain1D(s.y, y, haveLo, lo, haveHi, hi));
    }
    for (double x : xs) {
      findBounds(x, cp, [](const Pos& p) { return p.x; },
                 [&](const Pos& p) { return p.z == s.z && p.y == s.y; }, haveLo, lo, haveHi, hi);
      gx[k].push_back(pointGain1D(s.x, x, haveLo, lo, haveHi, hi));
    }
  }
}

std::vector<double> calcF(double p, const std::vector<double>& w, const Table& g) {
  std::vector<double> f(g.size(), 0.0);
  for (size_t k = 0; k < g.size(); ++k) {
    double sum = 0;
    for (size_t i = 0; i < w.size(); ++i) sum += std::pow(g[k][i] * w[i], p);
    f[k] = sum < kNeg130dBLim ? 0.0 : sum;
  }
  return f;
}

std::vector<double> safeNorm(const std::vector<double>& v) {
  double len = 0;
  for (double x : v) len += x * x;
  len = std::sqrt(len);
  std::vector<double> out(v.size(), 0.0);
  if (len > 1e-16)
    for (size_t i = 0; i < v.size(); ++i) out[i] = v[i] / len;
  return out;
}

}  // namespace

std::vector<double> extentGains(const std::vector<Pos>& cp, Pos o, double size_x, double size_y,
                                double size_z) {
  double xo = o.x, yo = o.y, zo = o.z;
  std::set<double> distinctZ;
  for (const auto& c : cp) distinctZ.insert(c.z);
  bool threePlanes = distinctZ.size() >= 3;

  const int Nx = kNumVs, Ny = kNumVs, Nz = threePlanes ? kNumVs : kNumVs / 2;
  std::vector<double> xs = linspace(-1.0, 1.0, Nx), ys = linspace(-1.0, 1.0, Ny), zs;
  if (threePlanes) {
    zs = linspace(-1.0, 1.0, Nz);
  } else {
    zs = linspace(0.0, 1.0, Nz);
    zo = std::max(0.0, zo);
  }

  double sx = std::max(scaleSize(size_x), 2.0 / (Nx - 1));
  double sy = std::max(scaleSize(size_y), 2.0 / (Ny - 1));
  double sz = std::max(scaleSize(size_z), 2.0 / (Nz - 1));
  double s_eff = sEff(cp, sx, sy, sz);
  double p = pOf(s_eff);
  int dim = dimOf(cp);

  double mu = muOf(dim, sx, sy, sz, xo, yo, zo);
  std::vector<double> wx = weights(xs, xo, 2.0 * sx, false);
  std::vector<double> wy = weights(ys, yo, 2.0 * sy, false);
  std::vector<double> wz = weights(zs, zo, sz, true);

  Table gpx, gpy, gpz;
  gPointSeparated(cp, xs, ys, zs, gpx, gpy, gpz);
  std::vector<double> fx = calcF(p, wx, gpx), fy = calcF(p, wy, gpy), fz = calcF(p, wz, gpz);

  size_t n = cp.size();
  std::vector<double> gInside(n);
  for (size_t k = 0; k < n; ++k) gInside[k] = fx[k] * fy[k] * fz[k];
  std::vector<double> gInsideNorm = safeNorm(gInside);

  std::vector<double> gSize(n);
  for (size_t k = 0; k < n; ++k) {
    double bFloor = std::pow(gpz[k].front() * wz.front(), p);
    double bCeil = std::pow(gpz[k].back() * wz.back(), p);
    double bLeft = std::pow(gpx[k].front() * wx.front(), p);
    double bRight = std::pow(gpx[k].back() * wx.back(), p);
    double bFront = std::pow(gpy[k].front() * wy.front(), p);
    double bBack = std::pow(gpy[k].back() * wy.back(), p);
    double gBound = bLeft * fy[k] * fz[k] + bRight * fy[k] * fz[k] + fx[k] * bFront * fz[k] +
                    fx[k] * bBack * fz[k] + fx[k] * fy[k] * bCeil + fx[k] * fy[k] * bFloor;
    gSize[k] = std::pow(gBound + mu * gInsideNorm[k], 1.0 / p);
  }
  std::vector<double> gSizeNorm = safeNorm(gSize);

  const double s_fade = 0.2;
  double alpha = 0.0, beta = 1.0;
  if (s_eff < s_fade) {
    alpha = std::cos((s_eff * kPi) / (s_fade * 2.0));
    beta = std::sin((s_eff * kPi) / (s_fade * 2.0));
  }

  Table px, py, pz;
  gPointSeparated(cp, {xo}, {yo}, {zo}, px, py, pz);
  std::vector<double> gTotal(n);
  for (size_t k = 0; k < n; ++k) {
    double gPoint = px[k][0] * py[k][0] * pz[k][0];
    gTotal[k] = alpha * gPoint + beta * gSizeNorm[k];
  }
  return safeNorm(gTotal);
}

// ---- ear/core/objectbased/gain_calc.py: GainCalc.render, cartesian branch --------------------

GainCalculator::GainCalculator(std::vector<Pos> positions)
    : positions_(std::move(positions)), point_(positions_) {}

void GainCalculator::gains(Pos object, double width, double height, double depth, double gain,
                           std::vector<double>& out) const {
  // coord_trans: Cartesian positions are clipped to the room box
  auto clip = [](double v) { return std::max(-1.0, std::min(1.0, v)); };
  Pos p{clip(object.x), clip(object.y), clip(object.z)};

  // allocentric_extent_pan
  if (width == 0 && height == 0 && depth == 0)
    point_.handle(p, out);
  else
    out = extentGains(positions_, p, width, height, depth);

  // no divergence: sqrt(dot([1], g^2)) = |g|; then nan_to_num and the object gain
  for (double& g : out) {
    g = std::fabs(g);
    if (std::isnan(g)) g = 0.0;
    g *= gain;
  }
}

}  // namespace allo
