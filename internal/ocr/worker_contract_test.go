package ocr

import (
	"os"
	"strings"
	"testing"
)

func TestPaddleWorkerContractIsPinnedAndPortable(t *testing.T) {
	b, err := os.ReadFile("worker.py")
	if err != nil {
		t.Fatal(err)
	}
	s := string(b)
	for _, want := range []string{
		`PADDLE_PDX_CACHE_HOME`,
		`PADDLE_PDX_MODEL_SOURCE`,
		`PP-OCRv6_small_det`,
		`PP-OCRv6_small_rec`,
		`use_doc_orientation_classify=False`,
		`use_doc_unwarping=False`,
		`use_textline_orientation=False`,
		`--device`,
		`paddle.is_compiled_with_cuda()`,
		`paddle.device.cuda.device_count()`,
		`device=ARGS.device`,
		`text_det_box_thresh=0.65`,
		`text_rec_score_thresh=0.60`,
		`"id"`,
		`"lines"`,
		`images_base64`,
		`len(encoded_batch) > 4`,
		`engine.predict(images`,
	} {
		if !strings.Contains(s, want) {
			t.Fatalf("worker missing contract marker %q", want)
		}
	}
	cacheAt := strings.Index(s, `PADDLE_PDX_CACHE_HOME`)
	importAt := strings.Index(s, `from paddleocr import PaddleOCR`)
	if cacheAt < 0 || importAt < 0 || cacheAt > importAt {
		t.Fatal("portable PaddleX cache must be configured before importing PaddleOCR")
	}
}
