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

// Room-relative ("allocentric") panning for Cartesian objects, as specified by ITU-R BS.2127.
//
// libear (ITU-R BS.2127's C++ implementation) does not implement this path: it throws
// not_implemented("cartesian"). This is a port of the EBU ADM Renderer reference implementation,
// ear 2.1.0 (Python, BSD-3-Clause-Clear, Copyright (c) 2018-2019 EBU ADM Renderer Authors;
// license above, also in THIRD_PARTY_NOTICES.md):
//   PointPanner   <- ear/core/point_source.py   AllocentricPanner
//   extentGains   <- ear/core/objectbased/allo_extent.py   get_gains
//   gains()       <- ear/core/objectbased/gain_calc.py   GainCalc.render, cartesian branch
// It is kept deliberately close to the Python so the two can be compared line by line, and is
// verified numerically against it (tools/allo_check.py).
//
// Not ported (not needed for DAMF yet): channel lock, zone exclusion, divergence, screen scaling.

#pragma once

#include <utility>
#include <vector>

namespace allo {

struct Pos {
  double x, y, z;
};

/// Point-source panner: separable sine/cosine balance between the speaker planes (z), the rows
/// within a plane (y) and the columns within a row (x).
class PointPanner {
 public:
  explicit PointPanner(const std::vector<Pos>& positions);
  /// Writes one gain per speaker into out (resized to the speaker count).
  void handle(const Pos& p, std::vector<double>& out) const;

 private:
  struct Entry {
    int index;
    Pos pos;
  };
  using Row = std::vector<Entry>;   // sorted by x
  using Plane = std::vector<Row>;   // sorted by y
  std::vector<Plane> tree_;         // sorted by z
  int n_;

  std::pair<int, int> findPlanes(double z) const;
  static std::pair<int, int> findRows(const Plane& plane, double y);
  static std::pair<int, int> findColumns(const Row& row, double x);
};

/// Gains for an object with size (width, height, depth all > 0 somewhere), allo_extent.get_gains.
std::vector<double> extentGains(const std::vector<Pos>& positions, Pos object, double sizeX,
                                double sizeY, double sizeZ);

/// The whole Cartesian branch of the BS.2127 object gain calculator for one layout.
class GainCalculator {
 public:
  /// positions: allocentric position of each non-LFE speaker, in output order.
  explicit GainCalculator(std::vector<Pos> positions);
  /// One gain per non-LFE speaker; gain is linear.
  void gains(Pos object, double width, double height, double depth, double gain,
             std::vector<double>& out) const;
  int speakerCount() const { return static_cast<int>(positions_.size()); }

 private:
  std::vector<Pos> positions_;
  PointPanner point_;
};

}  // namespace allo
