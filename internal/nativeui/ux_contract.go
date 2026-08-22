package nativeui

import "strings"

type workflowUX struct {
	Title string
	Help  string
}

var workflowUXByPage = map[int]workflowUX{
	pageSubtitle: {
		Title: "Tải phụ đề Bilibili",
		Help:  "Dán link video, bấm Kiểm tra để lấy danh sách track, chọn định dạng và thư mục rồi tải.",
	},
	pageVideo: {
		Title: "Tải video Bilibili",
		Help:  "Dán link video, bấm Kiểm tra để lấy chất lượng khả dụng, sau đó chọn nội dung, tốc độ và container.",
	},
	pageOCR: {
		Title: "OCR phụ đề tiếng Trung",
		Help:  "Chọn video, đặt vùng phụ đề, Test OCR trước khi quét. Auto tự chọn số luồng theo máy; Pause luôn lưu checkpoint an toàn.",
	},
	pageEditor: {
		Title: "Chỉnh video theo nhiều vùng",
		Help:  "Chọn video rồi kéo trực tiếp trên preview hoặc dùng preset. Mỗi vùng có hiệu ứng và khoảng thời gian riêng.",
	},
	pageSettings: {
		Title: "Cài đặt, đăng nhập và hỗ trợ",
		Help:  "Quản lý thư mục lưu, đăng nhập Bilibili bằng Cookie/QR, cập nhật, dọn dữ liệu và gửi báo lỗi.",
	},
}

// Native tooltips are intentionally short. Long explanations belong in the
// page help text so a new user can understand a workflow without hovering.
var tooltipTextByKey = map[string]string{
	"sub_url":         "Link Bilibili dạng https://www.bilibili.com/video/... hoặc BV/AV được ứng dụng hỗ trợ.",
	"sub_analyze":     "Đọc metadata và danh sách track phụ đề trước khi tải.",
	"sub_track":       "Chọn track phụ đề. Chính chủ được ghi rõ; track AI được đánh dấu AI.",
	"sub_format":      "SRT dùng cho dựng phim; TXT chỉ giữ nội dung; JSON giữ dữ liệu gốc.",
	"sub_download":    "Chỉ bật sau khi đã kiểm tra link và có track hợp lệ.",
	"sub_cancel":      "Hủy tác vụ tải phụ đề đang chạy.",
	"video_url":       "Link Bilibili cần tải.",
	"video_analyze":   "Đọc tiêu đề và các chất lượng khả dụng.",
	"video_quality":   "Chất lượng thực tế phụ thuộc quyền truy cập/tài khoản Bilibili.",
	"video_mode":      "Video + Audio tải đầy đủ; hai chế độ còn lại chỉ giữ một thành phần.",
	"video_speed":     "Ổn định ưu tiên tương thích; Nhanh/Turbo tăng song song khi nguồn cho phép.",
	"video_container": "MP4 tương thích rộng; MKV linh hoạt hơn với một số codec/track.",
	"ocr_pick":        "Chọn video local. Player native không phụ thuộc codec của trình duyệt.",
	"ocr_preset":      "Đặt nhanh ROI khu vực phụ đề ở phần dưới khung hình.",
	"ocr_timeline":    "Kéo để kiểm tra video; danh sách phụ đề sẽ tự chọn cue gần timestamp này.",
	"ocr_roi":         "Tọa độ phần trăm 0–100. Có thể kéo trực tiếp trên preview để đặt ROI.",
	"ocr_mode":        "Chính xác lấy mẫu dày hơn; Cân bằng là mặc định thực dụng; Nhanh giảm số khung.",
	"ocr_sensitivity": "Nhạy giữ nhiều candidate hơn; Ít nhạy giảm OCR rác nhưng có thể bỏ chữ mờ.",
	"ocr_device":      "Auto ưu tiên NVIDIA GPU khi khả dụng; CPU+GPU là chế độ thủ công.",
	"ocr_parallel":    "Auto benchmark 1→2→4→8→16 và dừng trước mức không an toàn. Manual dùng đúng số lane chọn.",
	"ocr_prepare":     "Chuẩn bị runtime/model OCR do BiliSub quản lý. Không dùng Python hệ thống.",
	"ocr_test":        "Nhận diện đúng frame hiện tại trong ROI để kiểm tra trước khi quét toàn video.",
	"ocr_start":       "Bắt đầu quét hoặc tiếp tục checkpoint của cùng video/ROI/cấu hình.",
	"ocr_pause":       "Chờ tất cả lane tới safe boundary rồi fsync checkpoint trước khi báo Paused.",
	"ocr_restart":     "Xóa checkpoint tương ứng và quét lại từ đầu.",
	"ocr_export":      "Xuất SRT tiếng Trung sau lớp lọc cuối; cue ngoài contract Chinese-only bị loại.",
	"ocr_cues":        "Click một cue để video nhảy tới thời gian bắt đầu cue đó.",
	"editor_preview":  "Kéo trên preview để tạo vùng mới; vùng được thêm vào danh sách bên phải.",
	"editor_presets":  "Preset tạo nhanh vùng phụ đề hoặc watermark rồi vẫn có thể chỉnh lại.",
	"editor_effect":   "Làm mờ, Mosaic hoặc Che đen cho vùng đang chọn.",
	"editor_scope":    "Bỏ chọn Toàn video để giới hạn hiệu ứng theo Bắt đầu/Kết thúc.",
	"editor_export":   "Xuất video với toàn bộ vùng trong danh sách theo cấu hình hiện tại.",
	"default_output":  "Thư mục mặc định được dùng cho Subtitle, Video, OCR và Editor.",
	"cookie":          "SESSDATA/Cookie được lưu bằng Windows DPAPI; không lưu plaintext.",
	"qr":              "QR được render ngay trong BiliSub Studio. Quét và xác nhận bằng ứng dụng Bilibili.",
	"update":          "Kiểm tra bản mới; cập nhật chỉ áp dụng sau khi tải và xác minh gói.",
	"cleanup":         "Dọn Temp/Cache. Không xóa file output của người dùng.",
	"bug":             "Mô tả cách tái hiện lỗi. Log gửi đi được sanitizer che cookie/token và đường dẫn user.",

	"sub_output":          "Thư mục chứa file phụ đề. Nút Mở dùng Explorer để kiểm tra file sau khi tải.",
	"video_output":        "Thư mục chứa video/audio sau khi tải và ghép.",
	"video_download":      "Bắt đầu tải với chất lượng, nội dung, tốc độ và container đang chọn.",
	"video_cancel":        "Hủy tác vụ tải video đang chạy; file tạm sẽ được BiliSub xử lý theo job lifecycle.",
	"ocr_play":            "Phát/Tạm dừng video preview native tại vị trí hiện tại.",
	"ocr_mute":            "Bật hoặc tắt âm thanh preview native.",
	"ocr_fullscreen":      "Mở preview toàn màn hình. Nhấn Esc để quay lại giao diện.",
	"ocr_clear":           "Xóa danh sách cue đã quét xong trong bộ nhớ. Không dùng được khi còn checkpoint Pause.",
	"ocr_output":          "Thư mục sẽ nhận file SRT sau khi quét hoàn tất.",
	"editor_pick":         "Chọn video local để preview và chỉnh nhiều vùng.",
	"editor_play":         "Phát/Tạm dừng preview Editor.",
	"editor_mute":         "Bật/Tắt tiếng preview Editor.",
	"editor_fullscreen":   "Mở preview Editor toàn màn hình; Esc để quay lại.",
	"editor_delete":       "Xóa vùng đang chọn khỏi danh sách.",
	"editor_undo":         "Hoàn tác thay đổi vùng gần nhất, tối đa theo lịch sử Editor.",
	"editor_region":       "Tọa độ phần trăm của vùng đang chọn. Có thể sửa số hoặc kéo trực tiếp trên preview.",
	"editor_strength":     "Độ mạnh hiệu ứng, giá trị hợp lệ 2–40.",
	"editor_timing":       "Nếu không áp dụng toàn video, đặt thời gian bắt đầu/kết thúc hoặc lấy từ vị trí player.",
	"editor_output":       "Thư mục và tên file video xuất.",
	"editor_regions":      "Danh sách tất cả vùng. Chọn một dòng để chỉnh đúng vùng đó.",
	"editor_cancel":       "Hủy tác vụ xuất video đang chạy.",
	"theme":               "Chuyển Dark/Light và lưu vào cấu hình BiliSub.",
	"default_output_pick": "Đổi thư mục lưu mặc định cho các workflow.",
	"default_output_open": "Mở thư mục lưu mặc định trong Explorer.",
	"cookie_save":         "Lưu Cookie/SESSDATA bằng Windows DPAPI và kiểm tra đăng nhập.",
	"cookie_delete":       "Xóa thông tin đăng nhập Bilibili đã lưu trên máy.",
	"auto_update":         "Cho phép BiliSub tự kiểm tra cập nhật theo cấu hình.",
	"reset_tools":         "Xóa/đặt lại tool do BiliSub quản lý; lần dùng sau có thể cần tải lại.",
	"remove_ocr":          "Xóa runtime/model OCR do BiliSub quản lý; không xóa video hay SRT của bạn.",
	"close_app":           "Đóng app an toàn. Nếu OCR đang chạy, BiliSub Pause và fsync checkpoint trước khi thoát.",
}

func uxForPage(page int) workflowUX { return workflowUXByPage[page] }
func tooltipFor(key string) string  { return tooltipTextByKey[key] }

func nonEmpty(s string) bool { return strings.TrimSpace(s) != "" }
