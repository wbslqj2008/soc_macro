"""Builds docs/소오콘_매크로_사용설명서.docx from the screenshots in docs/images.

Run:  python build_manual.py   (needs: pip install python-docx)
"""
from pathlib import Path

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml.ns import qn
from docx.shared import Cm, Pt, RGBColor

HERE = Path(__file__).parent
IMAGES = HERE / "images"
OUT = HERE / "소오콘_매크로_사용설명서.docx"
FONT = "맑은 고딕"
ACCENT = RGBColor(0x1F, 0x6F, 0xD1)

doc = Document()
sec = doc.sections[0]
sec.left_margin = sec.right_margin = Cm(2.2)
sec.top_margin = sec.bottom_margin = Cm(2.0)


for name, size in [("Normal", 10.5), ("Heading 1", 16), ("Heading 2", 13), ("Heading 3", 11.5), ("Title", 26)]:
    st = doc.styles[name]
    st.font.name = FONT
    st.element.get_or_add_rPr().get_or_add_rFonts().set(qn("w:eastAsia"), FONT)
    st.font.size = Pt(size)
    if name != "Normal":
        st.font.color.rgb = ACCENT if name != "Title" else RGBColor(0x20, 0x20, 0x20)
doc.styles["Normal"].paragraph_format.space_after = Pt(4)
doc.styles["Normal"].paragraph_format.line_spacing = 1.25


def p(text="", bold=False, size=None, color=None, align=None, after=None):
    para = doc.add_paragraph()
    if text:
        run = para.add_run(text)
        run.bold = bold
        if size:
            run.font.size = Pt(size)
        if color:
            run.font.color.rgb = color
    if align:
        para.alignment = align
    if after is not None:
        para.paragraph_format.space_after = Pt(after)
    return para


def bullet(text, level=0, bold_prefix=None):
    para = doc.add_paragraph(style="List Bullet" if level == 0 else "List Bullet 2")
    if bold_prefix:
        para.add_run(bold_prefix).bold = True
    para.add_run(text)
    return para


def step(_n, text):
    para = doc.add_paragraph(style="List Number")
    para.add_run(text)
    return para


def image(file, caption, width=15.5):
    path = IMAGES / file
    doc.add_picture(str(path), width=Cm(width))
    doc.paragraphs[-1].alignment = WD_ALIGN_PARAGRAPH.CENTER
    cap = p(caption, size=9, color=RGBColor(0x70, 0x70, 0x70), align=WD_ALIGN_PARAGRAPH.CENTER, after=10)
    return cap


def table(header, rows, widths=None):
    t = doc.add_table(rows=1, cols=len(header))
    t.style = "Light Grid Accent 1"
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    for i, h in enumerate(header):
        cell = t.rows[0].cells[i]
        cell.text = ""
        cell.paragraphs[0].add_run(h).bold = True
    for row in rows:
        cells = t.add_row().cells
        for i, v in enumerate(row):
            cells[i].text = v
    if widths:
        for row in t.rows:
            for i, w in enumerate(widths):
                row.cells[i].width = Cm(w)
    doc.add_paragraph()
    return t


def note(title, text):
    t = doc.add_table(rows=1, cols=1)
    t.style = "Light Shading Accent 1"
    cell = t.rows[0].cells[0]
    cell.text = ""
    para = cell.paragraphs[0]
    para.add_run(title + "  ").bold = True
    para.add_run(text)
    doc.add_paragraph()


# ───────────────────────── 표지 ─────────────────────────
for _ in range(6):
    p()
p("소오콘 매크로", bold=True, size=30, align=WD_ALIGN_PARAGRAPH.CENTER, after=6)
p("사용 설명서", size=18, color=RGBColor(0x55, 0x55, 0x55), align=WD_ALIGN_PARAGRAPH.CENTER, after=40)
p("소드 오브 콘발라리아 PC 자동화 도구", size=11, align=WD_ALIGN_PARAGRAPH.CENTER)
p("버전 0.1.0", size=10, color=RGBColor(0x80, 0x80, 0x80), align=WD_ALIGN_PARAGRAPH.CENTER)
doc.add_page_break()

# ───────────────────────── 목차 ─────────────────────────
doc.add_heading("목차", level=1)
for line in ["1. 소개", "2. 준비 사항", "3. 설치와 실행", "4. 라이센스 키 입력", "5. 대시보드 (시작과 정지)",
             "6. 탐험의 여정", "7. 일일퀘스트와 글로리 전장 (프로)", "8. 설정", "9. 사용 중 주의사항",
             "10. 문제 해결", "11. 라이센스 관리", "12. 유의사항"]:
    p(line, after=2)
doc.add_page_break()

# ───────────────────────── 1 ─────────────────────────
doc.add_heading("1. 소개", level=1)
p("소오콘 매크로는 소드 오브 콘발라리아 PC 버전의 반복 컨텐츠를 자동으로 진행하는 프로그램입니다. "
  "게임 화면을 읽어 지금 어떤 화면인지 판단하고, 알맞은 버튼을 대신 눌러 줍니다. "
  "어느 화면에서 시작해도 이어서 진행합니다.")
doc.add_heading("주요 기능", level=2)
bullet("이벤트 탐험의 여정 무한 반복 (축복 자동 선택, 패배 시 배치를 바꿔 재도전, 행동력 자동 보충)", bold_prefix="탐험의 여정: ")
bullet("엇갈린 세계 일일 컨텐츠 7종 자동 소탕/전투", bold_prefix="일일퀘스트: ")
bullet("알림판의 글로리 전장 하루 5회 자동 도전", bold_prefix="글로리 전장: ")
bullet("게임이 꺼져 있으면 자동 실행, 원하는 해상도로 창 크기 맞춤", bold_prefix="편의 기능: ")
doc.add_heading("라이센스 종류", level=2)
table(["종류", "사용할 수 있는 기능"],
      [["베이직", "탐험의 여정"],
       ["프로", "탐험의 여정 + 일일퀘스트 + 글로리 전장 (모든 기능)"]], widths=[3.5, 12])

# ───────────────────────── 2 ─────────────────────────
doc.add_heading("2. 준비 사항", level=1)
table(["항목", "내용"], [
    ["운영체제", "Windows 10 / 11 (64비트)"],
    ["별도 설치", "없음. 실행에 필요한 런타임이 프로그램 안에 포함되어 있어 .NET 등을 따로 설치하지 않아도 됩니다."],
    ["한국어 문자 인식", "Windows 내장 한국어 OCR을 사용합니다. 한국어 Windows에는 기본으로 들어 있습니다.\n영문 Windows라면: 설정 > 시간 및 언어 > 언어 및 지역 > 한국어 추가"],
    ["인터넷", "실행할 때와 매크로를 시작할 때 라이센스를 확인합니다."],
    ["게임 설정", "게임 언어 한국어 / 화면 비율 16:9 / 창 모드 권장"],
], widths=[3.5, 12])
note("참고", "Windows 디스플레이 배율(100%, 125% 등)과 듀얼 모니터는 자동으로 처리되므로 따로 맞출 필요가 없습니다.")

# ───────────────────────── 3 ─────────────────────────
doc.add_heading("3. 설치와 실행", level=1)
step(1, "받은 압축 파일을 원하는 폴더에 풉니다. (예: 바탕화면\\SocMacro)")
step(2, "폴더 안의 SocMacro.exe를 실행합니다.")
step(3, "「이 앱이 디바이스를 변경할 수 있도록 허용하시겠어요?」 창이 뜨면 [예]를 누릅니다. "
        "게임에 마우스 입력을 보내기 위해 관리자 권한이 필요합니다.")
note("팁", "Windows Defender SmartScreen이 「Windows의 PC 보호」 창을 띄우면 [추가 정보] → [실행]을 누르세요. "
     "처음 실행은 준비 과정 때문에 조금 느릴 수 있습니다.")
p("설정과 기록은 프로그램 폴더 안에 저장됩니다.")
bullet("settings.json: 모든 설정 (프로그램을 닫을 때와 시작할 때 자동 저장)")
bullet("logs 폴더: 날짜별 진행 기록과 문제 발생 시 스크린샷")

# ───────────────────────── 4 ─────────────────────────
doc.add_heading("4. 라이센스 키 입력", level=1)
p("처음 실행하면 라이센스 키 입력 창이 뜹니다.")
image("01_login.png", "그림 1. 라이센스 키 입력 창", width=9)
step(1, "받은 키를 입력합니다. (예: XXXX-XXXX-XXXX) 대소문자와 「-」는 생략해도 됩니다.")
step(2, "「이 PC에서 키 기억하기」를 켜 두면 다음부터는 키를 다시 입력하지 않아도 됩니다.")
step(3, "[확인]을 누르면 서버에서 키를 확인한 뒤 메인 화면이 열립니다.")
note("안내", "「등록되지 않은 라이센스 키입니다」가 나오면 키를 다시 확인하거나 판매자에게 문의해 주세요.")

# ───────────────────────── 5 ─────────────────────────
doc.add_heading("5. 대시보드 (시작과 정지)", level=1)
image("02_dashboard.png", "그림 2. 대시보드")
table(["항목", "설명"], [
    ["탐험 시작 (F9)", "탐험의 여정 반복을 시작합니다."],
    ["일일퀘스트", "체크한 일일퀘스트와 글로리 전장을 진행합니다. (프로)"],
    ["정지 (F10)", "언제든 매크로를 멈춥니다. 마우스·키보드 차단 중에도 F10은 항상 동작합니다."],
    ["상태 / 현재 단계", "지금 무엇을 하고 있는지 표시합니다. (예: 전투 3/7 진행 중)"],
    ["완료한 탐험 / 실행 시간 / 행동력", "이번 실행의 진행 현황입니다."],
    ["최근 기록", "최근 진행 기록 몇 줄을 보여 줍니다. 전체 기록은 「로그」 메뉴에서 볼 수 있습니다."],
], widths=[4.5, 11])
p("매크로를 시작하면 프로그램 창이 자동으로 최소화되고 게임 창을 조작합니다. "
  "확인이 필요한 일(행동력 부족, 잠긴 스테이지 등)이 생기면 대시보드 위쪽에 노란 알림이 쌓입니다.")

# ───────────────────────── 6 ─────────────────────────
doc.add_heading("6. 탐험의 여정", level=1)
doc.add_heading("시작 전 준비", level=2)
bullet("게임에서 탐험의 여정 이벤트에 한 번 이상 들어가 본 상태여야 합니다.")
bullet("출전 편성을 미리 맞춰 두세요. 매크로는 저장된 편성 그대로 출전합니다.")
bullet("게임은 로비, 이벤트 화면, 진행 중인 맵 등 어디에 있어도 됩니다. 진행 중인 탐험은 이어서 진행합니다.")
image("03_journey.png", "그림 3. 탐험의 여정 설정 (위쪽)")
doc.add_heading("진행", level=2)
table(["설정", "설명"], [
    ["사용", "켜져 있어야 탐험 시작 시 진행합니다."],
    ["난이도", "입장할 스테이지 (난이도 1 ~ 스테이지 5)"],
    ["탐험 장르", "쏜살같이 / 견고함 / 화력 집중"],
    ["반복 횟수", "0이면 정지할 때까지 계속 반복합니다."],
    ["고급 보상 받기", "켜면 고급 보상권이 있을 때 고급 보상으로, 없으면 클리어 포인트로 교환합니다."],
], widths=[4, 11.5])
doc.add_heading("축복 선택", level=2)
p("축복 카드 3장의 설명을 읽고, 우선 키워드가 들어간 카드를 고릅니다.")
bullet("한 줄에 하나씩 입력하고, 위에 있을수록 우선합니다. 띄어쓰기는 무시합니다.", bold_prefix="우선 키워드: ")
bullet("이 글자가 들어간 카드는 고르지 않습니다. 단점이 붙은 카드를 피할 때 씁니다.", bold_prefix="제외 키워드: ")
bullet("어느 카드에도 키워드가 없으면 고를 위치(첫째/둘째/셋째)입니다.", bold_prefix="키워드가 없을 때: ")
note("추천 제외 키워드", "상실, 받는 대미지가, 감소한다  (단, 「감소한다」는 「받는 대미지 감소」 같은 좋은 카드도 걸러질 수 있으니 취향에 맞게 조정하세요.)")
image("04_journey2.png", "그림 4. 탐험의 여정 설정 (아래쪽)")
doc.add_heading("패배 시", level=2)
bullet("패배하면 「전투 종료」 후 같은 전투에 다시 도전합니다. 이때 아군 두 명씩 무작위로 자리를 바꿉니다.")
bullet("같은 전투에서 지정한 횟수만큼 지면 그 탐험을 포기하고 처음부터 다시 시작합니다.", bold_prefix="패배 허용 횟수: ")
bullet("재도전 때 서로 자리를 바꿀 아군 쌍의 수입니다. 같은 캐릭터를 두 번 옮기지 않으므로 원래 배치로 돌아가지 않습니다.", bold_prefix="자리 바꿀 쌍 수: ")
doc.add_heading("행동력 (존재의 힘)", level=2)
p("한 판에 행동력 120이 필요합니다. 부족하면 아래 순서로 보충합니다.")
step(1, "존재의 힘 결정 사용 (켜져 있고, 보유한 경우 +100)")
step(2, "희망의 결정석으로 구매 (하루 최대 횟수 설정, 회당 +120 / 결정석 60·120·200·320)")
step(3, "둘 다 안 되면 행동력이 120이 될 때까지 기다렸다가 이어서 진행")
p("「회복 대기 중 입력 차단 해제」를 켜 두면, 기다리는 동안에는 PC를 자유롭게 쓸 수 있습니다.")

# ───────────────────────── 7 ─────────────────────────
doc.add_heading("7. 일일퀘스트와 글로리 전장 (프로)", level=1)
p("출항 → 엇갈린 세계의 일일 컨텐츠를 체크한 것만 위에서부터 차례로 진행합니다. "
  "대시보드의 [일일퀘스트] 또는 이 페이지 오른쪽 위의 [일일퀘스트 시작]으로 실행합니다.")
image("05_daily.png", "그림 5. 일일퀘스트 설정 (위쪽)")
doc.add_heading("공통 설정", level=2)
bullet("소탕을 즉시 완료합니다. 통조림이 없거나 부족하면 「계속 소탕」으로 남은 횟수를 채웁니다. "
       "끄면 항상 계속 소탕을 사용합니다.", bold_prefix="문어 통조림 사용: ")
bullet("탐험의 여정을 돌리는 중에 00시가 지나면, 진행 중인 탐험을 마치고 일일퀘스트를 한 뒤 다시 탐험으로 돌아갑니다.",
       bold_prefix="탐험 중 00시가 지나면 일일퀘스트 실행: ")
doc.add_heading("컨텐츠별 설정", level=2)
table(["컨텐츠", "진행 방식", "설정"], [
    ["타로의 자취", "마지막 클리어 스테이지 소탕 (하루 1회)", "요일별로 1~5 중 선택"],
    ["장비 시련", "마지막 클리어 스테이지 소탕 (하루 1회)", "요일별로 1~3 중 선택"],
    ["기억 회복", "마지막 스테이지 전투 (하루 1회, 기존 배치)", "-"],
    ["실전 훈련", "마지막 클리어 스테이지 소탕", "횟수"],
    ["진급 시련", "마지막 클리어 스테이지 소탕", "횟수, 요일별로 수호자/침입자/파멸자/분쇄자/감시자 선택"],
    ["뭇별의 시련", "마지막 클리어 스테이지 소탕", "횟수, 요일별로 1~3 선택"],
    ["제작 시련", "마지막 클리어 스테이지 소탕", "횟수"],
], widths=[3, 6.3, 6.2])
note("요일 설정", "타로의 자취와 장비 시련은 요일마다 열리는 단계가 다릅니다. 그날 잠긴 단계를 고르면 알림을 띄우고 건너뜁니다. "
     "기본값은 게임의 요일 일정에 맞춰져 있고, 일요일은 모든 단계가 열립니다. 「건너뜀」을 고르면 그 요일에는 진행하지 않습니다.")
note("행동력", "소탕에 필요한 행동력이 부족하면 가능한 횟수만 진행하고 대시보드에 알림을 띄웁니다. 오늘 이미 끝낸 컨텐츠는 자동으로 건너뜁니다.")
image("06_daily2.png", "그림 6. 일일퀘스트 설정 (아래쪽) · 글로리 전장")
doc.add_heading("글로리 전장", level=2)
p("일일퀘스트 다음에 알림판 → 글로리 전장에서 자동으로 도전합니다.")
bullet("쉬움(×1) / 일반(×1.3) / 어려움(×1.8)", bold_prefix="난이도: ")
bullet("1~5회 (하루 최대 5회). 게임의 남은 도전 횟수가 0이면 멈춥니다.", bold_prefix="횟수: ")
bullet("캐릭터 배치 → 시작 → 자동 전투 → 결과·티어 변동 화면 넘기기 → 다음 도전 순서로 진행합니다. "
       "전투 길이와 관계없이 화면을 보고 넘어갑니다.", bold_prefix="진행: ")
p("글로리 전장만 돌리려면 일일 컨텐츠를 모두 끄고 글로리 전장만 켠 뒤 [일일퀘스트 시작]을 누르세요.")

# ───────────────────────── 8 ─────────────────────────
doc.add_heading("8. 설정", level=1)
image("07_settings.png", "그림 7. 설정")
table(["설정", "설명"], [
    ["실행 방식", "게임이 꺼져 있을 때 공식 런처 / Steam 중 어떤 것으로 실행할지"],
    ["공식 런처 경로", "공식 런처 설치 위치의 XD.exe (기본: C:\\Program Files\\SwordofConvallaria\\SoCLauncher\\XD.exe)"],
    ["게임 자동 실행", "게임이 꺼져 있거나 도중에 튕기면 자동으로 다시 실행하고 로그인 화면을 넘깁니다."],
    ["화면 모드", "게임에서 쓰는 화면 모드 (창 모드 권장)"],
    ["게임 해상도", "「자동」은 현재 창 크기 그대로 사용. 창 모드에서 1280x720, 1600x900, 1920x1080 등을 고르면 시작할 때 게임 창 크기를 맞춥니다. 16:9 비율을 사용하세요."],
    ["게임 화면 감지", "현재 게임 창 크기와 Windows 배율을 확인합니다."],
    ["동작 중 마우스·키보드 차단", "매크로가 동작하는 동안 실수로 게임을 건드리지 않도록 입력을 막습니다. F10(정지)은 항상 동작합니다."],
], widths=[4.5, 11])

# ───────────────────────── 9 ─────────────────────────
doc.add_heading("9. 사용 중 주의사항", level=1)
bullet("매크로가 게임 창을 클릭할 때 게임 창이 맨 앞으로 옵니다. 진행 중에는 다른 작업을 하지 않는 것을 권장합니다.")
bullet("게임 창을 최소화하지 마세요. (다른 창에 가려지는 것은 괜찮습니다.)")
bullet("PC 절전 모드와 화면 보호기를 꺼 두세요. 절전에 들어가면 매크로가 멈춥니다.")
bullet("게임 안에서 Alt 키는 「뒤로 가기」로 동작하므로, 진행 중에 Alt+Tab 등을 누르지 마세요.")
bullet("멈추고 싶을 때는 언제든 F10을 누르세요.")

# ───────────────────────── 10 ─────────────────────────
doc.add_heading("10. 문제 해결", level=1)
image("08_log.png", "그림 8. 로그")
p("「로그」 메뉴에서 진행 기록 전체를 볼 수 있고, [로그 폴더 열기]로 기록 파일과 문제 화면 스크린샷(logs\\shots)을 열 수 있습니다. "
  "문의할 때 이 파일들을 함께 보내 주시면 빠르게 확인할 수 있습니다.")
table(["증상", "해결 방법"], [
    ["「서버에 연결할 수 없습니다」", "인터넷 연결을 확인하세요. 방화벽이 프로그램을 막고 있지 않은지 확인하세요."],
    ["「한국어 OCR을 사용할 수 없습니다」", "Windows 설정 > 시간 및 언어 > 언어 및 지역에서 한국어를 추가하세요."],
    ["「게임이 실행 중이 아닙니다」", "게임을 먼저 켜거나, 설정에서 「게임 자동 실행」을 켜세요."],
    ["클릭이 엉뚱한 곳에 됨", "게임 화면 비율이 16:9인지 확인하세요. 설정 > 게임 화면 감지로 확인할 수 있습니다."],
    ["「알 수 없는 화면」이 반복됨", "게임에 예상하지 못한 팝업이 떠 있을 수 있습니다. 팝업을 닫고 다시 시작하세요. logs\\shots의 스크린샷을 보내 주세요."],
    ["「○○ 잠겨 있어 건너뛰었습니다」", "일일퀘스트의 요일 설정을 확인하세요."],
    ["「행동력이 부족합니다」", "행동력을 보충하거나 소탕 횟수를 줄이세요."],
    ["일일퀘스트 버튼이 회색", "베이직 키입니다. 일일퀘스트와 글로리 전장은 프로 키가 필요합니다."],
    ["실행이 안 되고 바로 꺼짐", "백신이 프로그램을 차단하고 있지 않은지 확인하세요. 프로그램 폴더의 crash.log를 보내 주세요."],
], widths=[5, 10.5])

# ───────────────────────── 11 ─────────────────────────
doc.add_heading("11. 라이센스 관리", level=1)
image("09_license.png", "그림 9. 라이센스")
bullet("현재 키와 종류(베이직/프로)를 보여 줍니다.")
bullet("[새로고침]: 서버에서 키 정보를 다시 확인합니다. (프로로 업그레이드된 경우 등)")
bullet("[다른 키 입력]: 저장된 키를 지우고 키 입력 창으로 돌아갑니다.")
p("매크로를 시작할 때마다 서버에서 키를 다시 확인하므로, 인터넷이 연결되어 있어야 합니다.")

# ───────────────────────── 12 ─────────────────────────
doc.add_heading("12. 유의사항", level=1)
bullet("이 프로그램은 게임사가 제공하거나 허가한 프로그램이 아닙니다. 게임 이용약관에 따라 자동화 프로그램 사용 시 "
       "계정 제재를 받을 수 있으며, 그 책임은 사용자에게 있습니다.")
bullet("게임 업데이트로 화면이 바뀌면 일부 기능이 동작하지 않을 수 있습니다.")
bullet("라이센스 키를 다른 사람과 공유하지 마세요.")

doc.save(OUT)
print(f"saved {OUT}")
