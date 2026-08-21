package qrcode

import (
	"errors"
	"fmt"
)

// Matrix encodes text as a QR Code Model 2, Version 10, error correction L.
// Version 10-L is deliberately fixed so the native Windows UI can render login
// QR codes without a browser, WebView, network QR service, or external runtime.
type Matrix struct {
	Size    int
	modules [][]bool
}

func (m Matrix) At(x, y int) bool {
	if x < 0 || y < 0 || x >= m.Size || y >= m.Size {
		return false
	}
	return m.modules[y][x]
}

const (
	version       = 10
	size          = 17 + 4*version // 57
	dataCodewords = 274
	eccPerBlock   = 18
)

// Encode uses QR byte mode. Version 10-L supports at most 271 input bytes in
// byte mode after mode/count/terminator overhead.
func Encode(text string) (Matrix, error) {
	data, err := makeData([]byte(text))
	if err != nil {
		return Matrix{}, err
	}
	codewords := interleave(data)
	bestScore := int(^uint(0) >> 1)
	var best [][]bool
	for mask := 0; mask < 8; mask++ {
		modules, isFunc := newMatrix()
		drawFunctionPatterns(modules, isFunc)
		drawFormatBits(modules, isFunc, mask)
		drawVersionBits(modules, isFunc)
		drawCodewords(modules, isFunc, codewords, mask)
		score := penalty(modules)
		if score < bestScore {
			bestScore = score
			best = modules
		}
	}
	if best == nil {
		return Matrix{}, errors.New("không tạo được QR")
	}
	return Matrix{Size: size, modules: best}, nil
}

func makeData(src []byte) ([]byte, error) {
	// 4 mode bits + 16 count bits for versions 10..40 + 8 bits per byte.
	if len(src) > 271 {
		return nil, fmt.Errorf("QR URL quá dài: %d byte (tối đa 271)", len(src))
	}
	bits := make([]bool, 0, dataCodewords*8)
	appendBits := func(val uint, n int) {
		for i := n - 1; i >= 0; i-- {
			bits = append(bits, ((val>>i)&1) != 0)
		}
	}
	appendBits(0x4, 4) // byte mode
	appendBits(uint(len(src)), 16)
	for _, b := range src {
		appendBits(uint(b), 8)
	}
	capBits := dataCodewords * 8
	term := 4
	if capBits-len(bits) < term {
		term = capBits - len(bits)
	}
	for i := 0; i < term; i++ {
		bits = append(bits, false)
	}
	for len(bits)%8 != 0 {
		bits = append(bits, false)
	}
	out := make([]byte, 0, dataCodewords)
	for i := 0; i < len(bits); i += 8 {
		var b byte
		for j := 0; j < 8; j++ {
			if bits[i+j] {
				b |= 1 << (7 - j)
			}
		}
		out = append(out, b)
	}
	pads := []byte{0xEC, 0x11}
	for len(out) < dataCodewords {
		out = append(out, pads[(len(out)-(len(bits)/8))&1])
	}
	return out, nil
}

func interleave(data []byte) []byte {
	// QR Version 10-L RS blocks: 2 x (86 total, 68 data) and
	// 2 x (87 total, 69 data). Each block has 18 ECC codewords.
	dataLens := []int{68, 68, 69, 69}
	blocks := make([][]byte, 4)
	ecc := make([][]byte, 4)
	at := 0
	for i, n := range dataLens {
		blocks[i] = append([]byte(nil), data[at:at+n]...)
		at += n
		ecc[i] = reedSolomon(blocks[i], eccPerBlock)
	}
	out := make([]byte, 0, 346)
	for col := 0; col < 69; col++ {
		for i := 0; i < 4; i++ {
			if col < len(blocks[i]) {
				out = append(out, blocks[i][col])
			}
		}
	}
	for col := 0; col < eccPerBlock; col++ {
		for i := 0; i < 4; i++ {
			out = append(out, ecc[i][col])
		}
	}
	return out
}

func gfMultiply(x, y int) int {
	z := 0
	for y > 0 {
		if y&1 != 0 {
			z ^= x
		}
		y >>= 1
		x <<= 1
		if x&0x100 != 0 {
			x ^= 0x11D
		}
	}
	return z
}

func reedSolomon(data []byte, degree int) []byte {
	gen := make([]int, degree)
	gen[degree-1] = 1
	root := 1
	for i := 0; i < degree; i++ {
		for j := 0; j < degree; j++ {
			gen[j] = gfMultiply(gen[j], root)
			if j+1 < degree {
				gen[j] ^= gen[j+1]
			}
		}
		root = gfMultiply(root, 2)
	}
	rem := make([]int, degree)
	for _, b := range data {
		factor := int(b) ^ rem[0]
		copy(rem, rem[1:])
		rem[degree-1] = 0
		for j := 0; j < degree; j++ {
			rem[j] ^= gfMultiply(gen[j], factor)
		}
	}
	out := make([]byte, degree)
	for i, v := range rem {
		out[i] = byte(v)
	}
	return out
}

func newMatrix() ([][]bool, [][]bool) {
	m := make([][]bool, size)
	f := make([][]bool, size)
	for y := range m {
		m[y] = make([]bool, size)
		f[y] = make([]bool, size)
	}
	return m, f
}
func setFunction(m, f [][]bool, x, y int, dark bool) {
	if x >= 0 && x < size && y >= 0 && y < size {
		m[y][x] = dark
		f[y][x] = true
	}
}

func drawFunctionPatterns(m, f [][]bool) {
	// Timing patterns first; finder patterns overwrite their ends.
	for i := 0; i < size; i++ {
		setFunction(m, f, 6, i, i%2 == 0)
		setFunction(m, f, i, 6, i%2 == 0)
	}
	drawFinder(m, f, 3, 3)
	drawFinder(m, f, size-4, 3)
	drawFinder(m, f, 3, size-4)
	centers := []int{6, 28, 50}
	for _, cy := range centers {
		for _, cx := range centers {
			// Alignment patterns are omitted only where they overlap one of the
			// three finder patterns. Centers on the timing row/column elsewhere
			// are real alignment patterns and deliberately overwrite timing bits.
			if (cx == 6 && (cy == 6 || cy == 50)) || (cy == 6 && cx == 50) {
				continue
			}
			drawAlignment(m, f, cx, cy)
		}
	}
	// Reserve format regions and permanent dark module. Values are written later.
	for i := 0; i < 9; i++ {
		if i != 6 {
			setFunction(m, f, 8, i, false)
			setFunction(m, f, i, 8, false)
		}
	}
	for i := 0; i < 8; i++ {
		setFunction(m, f, size-1-i, 8, false)
		setFunction(m, f, 8, size-1-i, false)
	}
	setFunction(m, f, 8, size-8, true)
	// Reserve version information areas.
	for i := 0; i < 6; i++ {
		for j := 0; j < 3; j++ {
			setFunction(m, f, size-11+j, i, false)
			setFunction(m, f, i, size-11+j, false)
		}
	}
}

func drawFinder(m, f [][]bool, cx, cy int) {
	for dy := -4; dy <= 4; dy++ {
		for dx := -4; dx <= 4; dx++ {
			x, y := cx+dx, cy+dy
			dist := abs(dx)
			if abs(dy) > dist {
				dist = abs(dy)
			}
			setFunction(m, f, x, y, dist != 2 && dist != 4)
		}
	}
}
func drawAlignment(m, f [][]bool, cx, cy int) {
	for dy := -2; dy <= 2; dy++ {
		for dx := -2; dx <= 2; dx++ {
			d := abs(dx)
			if abs(dy) > d {
				d = abs(dy)
			}
			setFunction(m, f, cx+dx, cy+dy, d != 1)
		}
	}
}

func drawFormatBits(m, f [][]bool, mask int) {
	data := (1 << 3) | mask // ECC level L = binary 01
	rem := data
	for i := 0; i < 10; i++ {
		rem = (rem << 1) ^ ((rem >> 9) * 0x537)
	}
	bits := ((data << 10) | rem) ^ 0x5412
	get := func(i int) bool { return ((bits >> i) & 1) != 0 }
	// Around top-left finder.
	for i := 0; i <= 5; i++ {
		setFunction(m, f, 8, i, get(i))
	}
	setFunction(m, f, 8, 7, get(6))
	setFunction(m, f, 8, 8, get(7))
	setFunction(m, f, 7, 8, get(8))
	for i := 9; i < 15; i++ {
		setFunction(m, f, 14-i, 8, get(i))
	}
	// Other copy.
	for i := 0; i < 8; i++ {
		setFunction(m, f, size-1-i, 8, get(i))
	}
	for i := 8; i < 15; i++ {
		setFunction(m, f, 8, size-15+i, get(i))
	}
	setFunction(m, f, 8, size-8, true)
}

func drawVersionBits(m, f [][]bool) {
	rem := version
	for i := 0; i < 12; i++ {
		rem = (rem << 1) ^ ((rem >> 11) * 0x1F25)
	}
	bits := (version << 12) | rem
	for i := 0; i < 18; i++ {
		dark := ((bits >> i) & 1) != 0
		a := size - 11 + i%3
		b := i / 3
		setFunction(m, f, a, b, dark)
		setFunction(m, f, b, a, dark)
	}
}

func maskBit(mask, x, y int) bool {
	switch mask {
	case 0:
		return (x+y)%2 == 0
	case 1:
		return y%2 == 0
	case 2:
		return x%3 == 0
	case 3:
		return (x+y)%3 == 0
	case 4:
		return (y/2+x/3)%2 == 0
	case 5:
		return (x*y)%2+(x*y)%3 == 0
	case 6:
		return ((x*y)%2+(x*y)%3)%2 == 0
	case 7:
		return ((x*y)%3+(x+y)%2)%2 == 0
	}
	return false
}

func drawCodewords(m, f [][]bool, code []byte, mask int) {
	bitIndex := 0
	upward := true
	for right := size - 1; right >= 1; right -= 2 {
		if right == 6 {
			right--
		}
		for vert := 0; vert < size; vert++ {
			y := vert
			if upward {
				y = size - 1 - vert
			}
			for j := 0; j < 2; j++ {
				x := right - j
				if f[y][x] {
					continue
				}
				dark := false
				if bitIndex < len(code)*8 {
					dark = ((code[bitIndex>>3] >> uint(7-(bitIndex&7))) & 1) != 0
					bitIndex++
				}
				if maskBit(mask, x, y) {
					dark = !dark
				}
				m[y][x] = dark
			}
		}
		upward = !upward
	}
}

func penalty(m [][]bool) int {
	score := 0
	// Runs and finder-like 1:1:3:1:1 patterns.
	for y := 0; y < size; y++ {
		runColor := m[y][0]
		runLen := 1
		for x := 1; x < size; x++ {
			if m[y][x] == runColor {
				runLen++
			} else {
				if runLen >= 5 {
					score += 3 + runLen - 5
				}
				runColor = m[y][x]
				runLen = 1
			}
		}
		if runLen >= 5 {
			score += 3 + runLen - 5
		}
		score += finderPenaltyLine(m[y])
	}
	for x := 0; x < size; x++ {
		line := make([]bool, size)
		for y := 0; y < size; y++ {
			line[y] = m[y][x]
		}
		runColor := line[0]
		runLen := 1
		for y := 1; y < size; y++ {
			if line[y] == runColor {
				runLen++
			} else {
				if runLen >= 5 {
					score += 3 + runLen - 5
				}
				runColor = line[y]
				runLen = 1
			}
		}
		if runLen >= 5 {
			score += 3 + runLen - 5
		}
		score += finderPenaltyLine(line)
	}
	for y := 0; y < size-1; y++ {
		for x := 0; x < size-1; x++ {
			c := m[y][x]
			if m[y][x+1] == c && m[y+1][x] == c && m[y+1][x+1] == c {
				score += 3
			}
		}
	}
	dark := 0
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			if m[y][x] {
				dark++
			}
		}
	}
	total := size * size
	k := abs(dark*20-total*10) / total
	score += k * 10
	return score
}
func finderPenaltyLine(line []bool) int {
	score := 0
	// 00001011101 and 10111010000 (finder pattern with four light modules).
	for i := 0; i+10 < len(line); i++ {
		a := !line[i] && !line[i+1] && !line[i+2] && !line[i+3] && line[i+4] && !line[i+5] && line[i+6] && line[i+7] && line[i+8] && !line[i+9] && line[i+10]
		b := line[i] && !line[i+1] && line[i+2] && line[i+3] && line[i+4] && !line[i+5] && line[i+6] && !line[i+7] && !line[i+8] && !line[i+9] && !line[i+10]
		if a || b {
			score += 40
		}
	}
	return score
}
func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
