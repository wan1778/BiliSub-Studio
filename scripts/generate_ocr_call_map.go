package main

import (
	"bytes"
	"flag"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const outputPath = "docs/engineering/OCR_CALL_MAP.generated.md"

type functionInfo struct {
	Name  string
	File  string
	Line  int
	Calls []string
}

func main() {
	check := flag.Bool("check", false, "fail if the generated OCR call map is stale")
	flag.Parse()

	root, err := os.Getwd()
	must(err)
	content, err := render(root)
	must(err)
	out := filepath.Join(root, outputPath)

	if *check {
		existing, err := os.ReadFile(out)
		if err != nil || !bytes.Equal(existing, content) {
			fmt.Println("OCR CALL MAP: FAIL (generated map is stale; run go run scripts/generate_ocr_call_map.go)")
			os.Exit(1)
		}
		fmt.Println("OCR CALL MAP: PASS")
		return
	}

	must(os.WriteFile(out, content, 0o644))
	fmt.Println("wrote", outputPath)
}

func render(root string) ([]byte, error) {
	fset := token.NewFileSet()
	dir := filepath.Join(root, "internal", "ocr")
	pkgs, err := parser.ParseDir(fset, dir, func(info os.FileInfo) bool {
		return !strings.HasSuffix(info.Name(), "_test.go")
	}, parser.SkipObjectResolution)
	if err != nil {
		return nil, err
	}
	pkg := pkgs["ocr"]
	if pkg == nil {
		return nil, fmt.Errorf("internal/ocr package not found")
	}

	var funcs []functionInfo
	for filename, file := range pkg.Files {
		rel, err := filepath.Rel(root, filename)
		if err != nil {
			return nil, err
		}
		rel = filepath.ToSlash(rel)
		for _, decl := range file.Decls {
			fn, ok := decl.(*ast.FuncDecl)
			if !ok || fn.Body == nil {
				continue
			}
			name := fn.Name.Name
			if fn.Recv != nil && len(fn.Recv.List) > 0 {
				name = receiverName(fn.Recv.List[0].Type) + "." + name
			}
			calls := collectCalls(fn.Body)
			funcs = append(funcs, functionInfo{
				Name:  name,
				File:  rel,
				Line:  fset.Position(fn.Pos()).Line,
				Calls: calls,
			})
		}
	}
	sort.Slice(funcs, func(i, j int) bool {
		if funcs[i].File != funcs[j].File {
			return funcs[i].File < funcs[j].File
		}
		return funcs[i].Line < funcs[j].Line
	})

	var b strings.Builder
	b.WriteString("# OCR generated symbol call map\n\n")
	b.WriteString("> GENERATED FROM CURRENT `internal/ocr` SOURCE by `scripts/generate_ocr_call_map.go`. Do not hand-edit.\n")
	b.WriteString("> Every production Go function in `internal/ocr` is listed with its source location and direct call expressions. `--check` blocks a stale map.\n\n")
	b.WriteString("## Ownership boundary\n\n")
	b.WriteString("```text\n")
	b.WriteString("web OCR controls\n")
	b.WriteString("  -> preview probe: MP4 H.264/HEVC/AV1 -> direct <video> attempt -> ocrDirectPlaybackReady -> idle Play/Mute; video.onerror -> FFmpeg-frame fallback with Play/Mute disabled\n")
	b.WriteString("  -> internal/api OCR handlers\n")
	b.WriteString("     -> internal/ocr Manager / Scanner\n")
	b.WriteString("        -> device detection + CPU/GPU runtime installer + private PaddleOCR worker(s)\n")
	b.WriteString("        -> RC13 parallel segment coordinator + bounded FFmpeg/NVDEC lanes + sparse visual gate + shared dynamic OCR worker pool + lane-local subtitle trackers + strict Chinese-only cue normalization + core/boundary reconciliation + schema-4 pause/resume checkpoint (schema 3 retained for legacy)\n")
	b.WriteString("```\n\n")
	b.WriteString(fmt.Sprintf("Production OCR Go functions: **%d**\n\n", len(funcs)))
	b.WriteString("| Function | Source | Direct calls |\n")
	b.WriteString("|---|---|---|\n")
	for _, fn := range funcs {
		calls := "_leaf / external state only_"
		if len(fn.Calls) > 0 {
			quoted := make([]string, 0, len(fn.Calls))
			for _, call := range fn.Calls {
				quoted = append(quoted, "`"+call+"`")
			}
			calls = strings.Join(quoted, ", ")
		}
		b.WriteString(fmt.Sprintf("| `%s` | `%s:%d` | %s |\n", fn.Name, fn.File, fn.Line, calls))
	}
	return []byte(b.String()), nil
}

func receiverName(expr ast.Expr) string {
	switch x := expr.(type) {
	case *ast.Ident:
		return x.Name
	case *ast.StarExpr:
		return receiverName(x.X)
	case *ast.IndexExpr:
		return receiverName(x.X)
	case *ast.IndexListExpr:
		return receiverName(x.X)
	default:
		return "receiver"
	}
}

func collectCalls(body *ast.BlockStmt) []string {
	set := map[string]struct{}{}
	ast.Inspect(body, func(n ast.Node) bool {
		call, ok := n.(*ast.CallExpr)
		if !ok {
			return true
		}
		name := callName(call.Fun)
		if name != "" && !isNoiseCall(name) {
			set[name] = struct{}{}
		}
		return true
	})
	out := make([]string, 0, len(set))
	for name := range set {
		out = append(out, name)
	}
	sort.Strings(out)
	return out
}

func callName(expr ast.Expr) string {
	switch x := expr.(type) {
	case *ast.Ident:
		return x.Name
	case *ast.SelectorExpr:
		left := exprName(x.X)
		if left == "" {
			return x.Sel.Name
		}
		return left + "." + x.Sel.Name
	default:
		return ""
	}
}

func exprName(expr ast.Expr) string {
	switch x := expr.(type) {
	case *ast.Ident:
		return x.Name
	case *ast.SelectorExpr:
		left := exprName(x.X)
		if left == "" {
			return x.Sel.Name
		}
		return left + "." + x.Sel.Name
	case *ast.StarExpr:
		return exprName(x.X)
	default:
		return ""
	}
}

func isNoiseCall(name string) bool {
	switch name {
	case "append", "cap", "close", "copy", "delete", "len", "make", "new", "panic", "print", "println", "recover":
		return true
	default:
		return false
	}
}

func must(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
