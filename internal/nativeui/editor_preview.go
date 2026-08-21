package nativeui

import (
	"math"

	"bilisubstudio/internal/nativeplayer"
	"bilisubstudio/internal/videoedit"
)

func editorPreviewFrame(src nativeplayer.Frame, regions []videoedit.Region) nativeplayer.Frame {
	if src.Width <= 0 || src.Height <= 0 || len(src.BGRA) < src.Width*src.Height*4 || len(regions) == 0 {
		return src
	}
	out := src
	out.BGRA = append([]byte(nil), src.BGRA...)
	for _, r := range regions {
		r = normalizeEditorRegion(r)
		if !r.Whole && (src.Time < r.Start || src.Time > r.End) {
			continue
		}
		x0 := clampInt(int(math.Round(r.X*float64(src.Width))), 0, src.Width-1)
		y0 := clampInt(int(math.Round(r.Y*float64(src.Height))), 0, src.Height-1)
		x1 := clampInt(int(math.Round((r.X+r.W)*float64(src.Width))), x0+1, src.Width)
		y1 := clampInt(int(math.Round((r.Y+r.H)*float64(src.Height))), y0+1, src.Height)
		switch r.Effect {
		case "cover":
			for y := y0; y < y1; y++ {
				for x := x0; x < x1; x++ {
					i := (y*src.Width + x) * 4
					out.BGRA[i], out.BGRA[i+1], out.BGRA[i+2] = 0, 0, 0
				}
			}
		case "mosaic":
			block := clampInt(r.Strength/2, 3, 20)
			pixelateBGRA(out.BGRA, src.Width, x0, y0, x1, y1, block)
		default:
			radius := clampInt(r.Strength/4, 1, 8)
			boxBlurBGRA(out.BGRA, src.Width, src.Height, x0, y0, x1, y1, radius)
		}
	}
	return out
}

func pixelateBGRA(p []byte, stride, x0, y0, x1, y1, block int) {
	for by := y0; by < y1; by += block {
		for bx := x0; bx < x1; bx += block {
			ex, ey := minPreview(bx+block, x1), minPreview(by+block, y1)
			var sb, sg, sr, sa, n int
			for y := by; y < ey; y++ {
				for x := bx; x < ex; x++ {
					i := (y*stride + x) * 4
					sb += int(p[i])
					sg += int(p[i+1])
					sr += int(p[i+2])
					sa += int(p[i+3])
					n++
				}
			}
			if n == 0 {
				continue
			}
			b, g, r, a := byte(sb/n), byte(sg/n), byte(sr/n), byte(sa/n)
			for y := by; y < ey; y++ {
				for x := bx; x < ex; x++ {
					i := (y*stride + x) * 4
					p[i], p[i+1], p[i+2], p[i+3] = b, g, r, a
				}
			}
		}
	}
}

func boxBlurBGRA(p []byte, width, height, x0, y0, x1, y1, radius int) {
	copySrc := append([]byte(nil), p...)
	for y := y0; y < y1; y++ {
		for x := x0; x < x1; x++ {
			var sb, sg, sr, sa, n int
			for yy := maxPreview(y0, y-radius); yy <= minPreview(y1-1, y+radius); yy++ {
				for xx := maxPreview(x0, x-radius); xx <= minPreview(x1-1, x+radius); xx++ {
					i := (yy*width + xx) * 4
					sb += int(copySrc[i])
					sg += int(copySrc[i+1])
					sr += int(copySrc[i+2])
					sa += int(copySrc[i+3])
					n++
				}
			}
			i := (y*width + x) * 4
			p[i], p[i+1], p[i+2], p[i+3] = byte(sb/n), byte(sg/n), byte(sr/n), byte(sa/n)
		}
	}
	_ = height
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func minPreview(a, b int) int {
	if a < b {
		return a
	}
	return b
}
func maxPreview(a, b int) int {
	if a > b {
		return a
	}
	return b
}
