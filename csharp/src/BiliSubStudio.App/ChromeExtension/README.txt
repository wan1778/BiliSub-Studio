BiliSub Bridge - Chrome extension
================================

Extension nay chi lam mot viec: doc cookie SESSDATA cua Bilibili trong Chrome
va gui sang BiliSub Studio khi ban bam "Dang nhap / Dong bo tu Chrome". Neu
Chrome chua dang nhap, extension mo passport.bilibili.com thanh tab Chrome de
ban tu dang nhap; sau do extension tu gui phien ve app. App khong mo cua so
dang nhap nhung.

Cai dat mot lan:
1. Mo Chrome, nhap chrome://extensions vao thanh dia chi.
2. Bat "Developer mode" (Che do danh cho nha phat trien).
3. Bam "Load unpacked" (Tai tien ich da giai nen).
4. Chon thu muc do app mo bang nut "Mo thu muc extension" (thu muc trong Data,
   chua file manifest.json va bridge-secret.js).
5. Kiem tra extension "BiliSub Bridge" dang duoc bat trong dung profile Chrome
   ma ban da dang nhap Bilibili.

Sau do quay lai BiliSub Studio, vao Cai dat -> Dang nhap -> "Dang nhap / Dong bo tu Chrome".

Luu y:
- Extension khong luu SESSDATA; moi lan dong bo no chi doc cookie hien tai.
- Extension khong doc mat khau, khong dang nhap thay ban.
- Neu Chrome co nhieu profile, hay cai extension trong profile dang dung Bilibili.
- Sau khi cap nhat BiliSub Studio, neu extension khong chay, mo chrome://extensions
  va bam Reload tren extension BiliSub Bridge.
