# AutoClick → Scan API

Client WinForms gửi dữ liệu **ngầm** khi bấm **Bắt đầu**. Không chờ response, lỗi API không chặn crawl.

Base URL: `https://scan.thuoc360.com`

`site` luôn lấy từ **dòng từ khóa đầu tiên**, lowercase. Phải đã có trong bảng `sitename` và khớp:

`^[a-z0-9][a-z0-9_-]{0,99}$`

Ví dụ từ khóa `hakoreview` / `hako` / `bánh mì` → `?site=hakoreview`.

Mỗi lần chạy, client gọi **2 loại API**:

1. `POST /api/auto-click-jobs` — **một lần**, toàn bộ form (endpoint mới, cần implement).
2. `POST /api/auto-click-keys` — **mỗi từ khóa một lần** (API cũ, giữ để bảng key vẫn nhận).

`Content-Type: application/json`

`app_version` hiện tại: `1.3.0`

---

## 1. POST `/api/auto-click-jobs` — nhận cả form

### URL

```
POST /api/auto-click-jobs?site={sitename}
```

### Query

| Tham số | Bắt buộc | Mô tả |
|---------|----------|--------|
| `site` | có | Tên site đã đăng ký (`hakoreview`, `thuoc360`, …). Dùng `api_require_site()`. |

### Body

| Field | Type | Bắt buộc | Max / ghi chú |
|-------|------|----------|----------------|
| `device_id` | string | không | 255. Client tự sinh, ví dụ `device-10db8da169df` |
| `device_name` | string | không | 255. Thường là `Environment.MachineName` |
| `app_version` | string | không | 64 |
| `note` | string | không | 500. Cố định `"from auto-click client"` |
| `form` | object | **có** | Toàn bộ ô form (kể cả nhóm crawl đã ẩn) |
| `browser` | object | **có** | Trình duyệt + profile đang chọn |
| `meta` | object | không | Client gửi `{ "source": "auto-click", "locale": "vi" }` |

#### `form`

| Field | Type | Bắt buộc | Ghi chú |
|-------|------|----------|---------|
| `keywords` | string[] | **có** | Mỗi dòng từ khóa. Phần tử đầu = `site` |
| `target_links` | string[] | **có** | Link / domain mục tiêu |
| `match_mode` | string | **có** | `contains` \| `domain` \| `exact` |
| `max_google_pages` | int | **có** | 1–20 |
| `delay_ms` | int | **có** | 200–20000 |
| `output_directory` | string | không | Đường dẫn local máy user. Nhóm crawl đã ẩn; mặc định `%USERPROFILE%\Documents\AutoClick\results` |
| `selectors` | object[] | không | CSS selector tùy chọn. Mặc định `[]` vì form ẩn |
| `save_html` | bool | **có** | Mặc định `true` |
| `save_csv` | bool | **có** | Mặc định `true` |
| `save_json` | bool | **có** | Mặc định `true` |

#### `form.selectors[]`

| Field | Type | Bắt buộc |
|-------|------|----------|
| `name` | string | có |
| `selector` | string | có |

#### `browser`

| Field | Type | Ghi chú |
|-------|------|---------|
| `kind` | string | Tên hiển thị, ví dụ `Chromium`, `Google Chrome` |
| `channel` | string | `chromium` \| `chrome` \| `msedge` |
| `profile_folder` | string | Tên thư mục profile (`Default`, `Profile 1`) |
| `profile_name` | string | Tên hiển thị trên ComboBox |

### Ví dụ request

```bash
curl -sS -X POST 'https://scan.thuoc360.com/api/auto-click-jobs?site=hakoreview' \
  -H 'Content-Type: application/json' \
  -d '{
    "device_id": "device-10db8da169df",
    "device_name": "DESKTOP-8CP5J82",
    "app_version": "1.3.0",
    "note": "from auto-click client",
    "form": {
      "keywords": ["hakoreview", "hako", "bánh mì"],
      "target_links": ["https://www.example.com"],
      "match_mode": "domain",
      "max_google_pages": 3,
      "delay_ms": 1500,
      "output_directory": "C:\\\\Users\\\\Tuantt\\\\Documents\\\\AutoClick\\\\results",
      "selectors": [],
      "save_html": true,
      "save_csv": true,
      "save_json": true
    },
    "browser": {
      "kind": "Chromium",
      "channel": "chromium",
      "profile_folder": "Default",
      "profile_name": "Default"
    },
    "meta": {
      "source": "auto-click",
      "locale": "vi"
    }
  }'
```

### Response đề xuất

**201 Created** (job mới) hoặc **200 OK** (cập nhật cùng device + cùng form hash, nếu bạn muốn upsert):

```json
{
  "success": true,
  "id": 12,
  "site": "hakoreview"
}
```

**Lỗi** — giữ format scan hiện tại:

```json
{ "success": false, "error": "Missing site parameter. Use ?site=your-site-name" }
```

| HTTP | Khi nào |
|------|---------|
| 400 | Thiếu `site`, `form`, `form.keywords`, `form.target_links`, hoặc `match_mode` sai |
| 403 | `site` chưa đăng ký / bị tắt |
| 405 | Không phải POST |
| 413 | Body quá lớn (khuyến nghị giới hạn 256 KB) |

### Bảng SQL đề xuất

```sql
CREATE TABLE IF NOT EXISTS auto_click_jobs (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  site_id INT UNSIGNED NOT NULL,
  site VARCHAR(100) NOT NULL,
  device_id VARCHAR(255) NULL,
  device_name VARCHAR(255) NULL,
  app_version VARCHAR(64) NULL,
  keywords_json JSON NOT NULL,
  target_links_json JSON NOT NULL,
  match_mode VARCHAR(32) NOT NULL,
  max_google_pages INT NOT NULL DEFAULT 3,
  delay_ms INT NOT NULL DEFAULT 1500,
  output_directory VARCHAR(1024) NULL,
  selectors_json JSON NULL,
  save_html TINYINT(1) NOT NULL DEFAULT 1,
  save_csv TINYINT(1) NOT NULL DEFAULT 1,
  save_json TINYINT(1) NOT NULL DEFAULT 1,
  browser_kind VARCHAR(64) NULL,
  browser_channel VARCHAR(64) NULL,
  profile_folder VARCHAR(128) NULL,
  profile_name VARCHAR(255) NULL,
  note VARCHAR(500) NULL,
  meta_json JSON NULL,
  ip VARCHAR(64) NULL,
  created_at DATETIME NOT NULL,
  PRIMARY KEY (id),
  KEY idx_site (site),
  KEY idx_device (device_id),
  KEY idx_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### PHP mẫu — `web/api/auto-click-jobs.php`

```php
<?php
declare(strict_types=1);

require_once __DIR__ . '/../includes/api_helpers.php';

if (($_SERVER['REQUEST_METHOD'] ?? '') === 'OPTIONS') {
    api_json(['ok' => true]);
}

if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
    api_error('Method not allowed.', 405);
}

$siteRow = api_require_site();
$raw = file_get_contents('php://input') ?: '';
if (strlen($raw) > 262144) {
    api_error('Payload too large.', 413);
}

$data = json_decode($raw, true);
if (!is_array($data)) {
    api_error('Invalid JSON body.');
}

$form = $data['form'] ?? null;
$browser = $data['browser'] ?? [];
if (!is_array($form)) {
    api_error('Missing form object.');
}

$keywords = $form['keywords'] ?? null;
$targets = $form['target_links'] ?? null;
$mode = strtolower(trim((string) ($form['match_mode'] ?? '')));

if (!is_array($keywords) || $keywords === []) {
    api_error('form.keywords is required.');
}
if (!is_array($targets) || $targets === []) {
    api_error('form.target_links is required.');
}
if (!in_array($mode, ['contains', 'domain', 'exact'], true)) {
    api_error('form.match_mode must be contains, domain, or exact.');
}

db_exec(
    'INSERT INTO auto_click_jobs (
        site_id, site, device_id, device_name, app_version,
        keywords_json, target_links_json, match_mode, max_google_pages, delay_ms,
        output_directory, selectors_json, save_html, save_csv, save_json,
        browser_kind, browser_channel, profile_folder, profile_name,
        note, meta_json, ip, created_at
    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
    [
        (int) $siteRow['id'],
        $siteRow['name'],
        substr((string) ($data['device_id'] ?? ''), 0, 255) ?: null,
        substr((string) ($data['device_name'] ?? ''), 0, 255) ?: null,
        substr((string) ($data['app_version'] ?? ''), 0, 64) ?: null,
        json_encode(array_values($keywords), JSON_UNESCAPED_UNICODE),
        json_encode(array_values($targets), JSON_UNESCAPED_UNICODE),
        $mode,
        (int) ($form['max_google_pages'] ?? 3),
        (int) ($form['delay_ms'] ?? 1500),
        substr((string) ($form['output_directory'] ?? ''), 0, 1024) ?: null,
        json_encode($form['selectors'] ?? [], JSON_UNESCAPED_UNICODE),
        !empty($form['save_html']) ? 1 : 0,
        !empty($form['save_csv']) ? 1 : 0,
        !empty($form['save_json']) ? 1 : 0,
        substr((string) ($browser['kind'] ?? ''), 0, 64) ?: null,
        substr((string) ($browser['channel'] ?? ''), 0, 64) ?: null,
        substr((string) ($browser['profile_folder'] ?? ''), 0, 128) ?: null,
        substr((string) ($browser['profile_name'] ?? ''), 0, 255) ?: null,
        substr((string) ($data['note'] ?? ''), 0, 500) ?: null,
        json_encode($data['meta'] ?? new stdClass(), JSON_UNESCAPED_UNICODE),
        $_SERVER['REMOTE_ADDR'] ?? null,
        date('Y-m-d H:i:s'),
    ]
);

api_json([
    'success' => true,
    'id' => (int) db_last_insert_id(),
    'site' => $siteRow['name'],
], 201);
```

Nhớ map route `/api/auto-click-jobs` → file này (cùng cách bạn đã map `/api/auto-click-keys`).

---

## 2. POST `/api/auto-click-keys` — mỗi từ khóa (API cũ, vẫn gửi)

Client **vẫn gọi** endpoint này, mỗi keyword một POST, để bảng key hiện tại không trống trong lúc chưa xong jobs.

```
POST /api/auto-click-keys?site={sitename}
```

| Field | Type | Bắt buộc | Max |
|-------|------|----------|-----|
| `key` | string | **có** | 255 (alias `user_key`) |
| `device_id` | string | không | 255 |
| `device_name` | string | không | 255 |
| `app_version` | string | không | 64 |
| `note` | string | không | 500 |
| `meta` | object | không | Lưu `meta_json` |

```bash
curl -sS -X POST 'https://scan.thuoc360.com/api/auto-click-keys?site=hakoreview' \
  -H 'Content-Type: application/json' \
  -d '{
    "key": "hakoreview",
    "device_id": "device-10db8da169df",
    "device_name": "DESKTOP-8CP5J82",
    "app_version": "1.3.0",
    "note": "from auto-click client",
    "meta": { "source": "auto-click", "locale": "vi" }
  }'
```

Với 3 từ khóa, server nhận 3 request: `hakoreview`, `hako`, `bánh mì`.

Nếu đã lưu `form.keywords` ở jobs, có thể **bỏ** gọi keys sau này — chỉ cần sửa client.

---

## Thứ tự client gửi

1. Một `POST /api/auto-click-jobs`
2. N lần `POST /api/auto-click-keys` (N = số từ khóa)

Timeout mỗi request: 12 giây. Client nuốt mọi lỗi (404 khi jobs chưa deploy cũng không sao).

---

## Checklist phía server

- [ ] Tạo bảng `auto_click_jobs`
- [ ] File `web/api/auto-click-jobs.php` + route
- [ ] `hakoreview` (và site khác) có trong `sitename` và `is_active = 1`
- [ ] CORS đã có trong `api_json()` (`POST`, `Content-Type`)
- [ ] Admin xem được `keywords_json`, `target_links_json`, `match_mode`, crawl flags
