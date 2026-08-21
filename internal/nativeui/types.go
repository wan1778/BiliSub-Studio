package nativeui

const (
	pageSubtitle = iota
	pageVideo
	pageOCR
	pageEditor
	pageSettings
)

type RunResult struct{ UpdatePath string }

type layoutBox struct {
	X, Y, W, H int
}

// captionLayout is shared by the Windows renderer and platform-independent
// regression tests. Every native caption is assigned an explicit rectangle;
// no label is allowed to rely on its CreateWindow default 0,0,10,10 geometry.
func captionLayout(page, x, pw, ch int) []layoutBox {
	_ = ch
	box := func(x, y, w int) layoutBox { return layoutBox{X: x, Y: y, W: w, H: 22} }
	switch page {
	case pageSubtitle:
		return []layoutBox{
			box(x+8, 82, pw-28),
			box(x+8, 178, pw/2-24),
			box(x+pw/2+8, 178, pw/2-28),
			box(x+8, 246, pw-28),
		}
	case pageVideo:
		q := pw / 4
		return []layoutBox{
			box(x+8, 82, pw-28),
			box(x+8, 178, q-20),
			box(x+q+8, 178, q-20),
			box(x+2*q+8, 178, q-20),
			box(x+3*q+8, 178, q-28),
			box(x+8, 246, pw-28),
		}
	case pageOCR:
		left := maxLayout(430, pw*52/100)
		rightX := x + left + 14
		rightW := pw - left - 22
		return []layoutBox{
			box(x+8, 78, left-20),
			box(rightX, 78, rightW-8),
			box(rightX, 158, rightW/2-8),
			box(rightX+rightW/2, 158, rightW/2-8),
			box(rightX, 220, rightW/2-8),
			box(rightX+rightW/2, 220, rightW/2-8),
			box(rightX, 378, rightW-8),
		}
	case pageEditor:
		left := maxLayout(430, pw*58/100)
		rightX := x + left + 14
		rightW := pw - left - 22
		return []layoutBox{
			box(x+8, 78, left-20),
			box(rightX, 78, rightW-8),
			box(rightX, 158, rightW/2-8),
			box(rightX+rightW/2, 158, rightW/2-8),
			box(rightX, 246, rightW-8),
			box(rightX, 326, rightW-8),
			box(rightX, 384, rightW-8),
			box(rightX, 442, rightW-8),
		}
	case pageSettings:
		return []layoutBox{
			box(x+8, 80, pw-28),
			box(x+8, 142, pw-28),
			box(x+8, 236, pw-28),
			box(x+8, 300, pw-28),
			box(x+8, 424, pw-28),
			box(x+8, 510, pw-28),
			box(x+8, 562, pw-28),
			box(x+8, 624, pw-28),
		}
	default:
		return nil
	}
}

func maxLayout(a, b int) int {
	if a > b {
		return a
	}
	return b
}
