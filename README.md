# Folderss Docker 모니터

[Folderss](https://github.com/zaruous/Folderss) 플러그인입니다. SSH로 접속한 서버의 **도커 컨테이너 상태**와 **호스트 CPU·메모리·디스크**를 한 창에서 보여 줍니다.
서버에는 아무것도 설치하지 않습니다.

`⋯ 메뉴 > 플러그인 > Docker 모니터`

![Docker 모니터 화면](docs/images/monitor.png)

커밋별 변경 내용과 화면은 [docs/변경사항](docs/변경사항/README.md)에 있습니다.

| 영역 | 내용 |
|---|---|
| 상단 막대 | 호스트 선택, [호스트 설정…], 갱신 주기(2·3·5·10·30초, 기본 3초), 자동 갱신, 새로고침, 프로젝트별 묶기 |
| 알림 줄 | 컨테이너 멈춤·재시작 반복·사라짐·다시 실행, unhealthy·회복, 디스크·호스트 메모리 90% 넘김을 직전 갱신과 비교해 알림. 창이 비활성이면 작업 표시줄 깜빡임. [기록]·[지우기]. 직접 조작한 컨테이너는 3분간 알리지 않음 |
| 컨테이너 표 | 이름·compose 프로젝트·상태·상세·CPU(호스트 전체 대비 %, 사용 코어 수)·메모리·네트워크·디스크 I/O·이미지·포트. 상태 색, unhealthy 줄 강조, CPU·메모리 80% 이상 강조. **compose 프로젝트별 묶음**(머리줄에 실행 수·CPU·메모리 합계, 눌러서 접기) |
| 추이 그래프 | 호스트 CPU(사용률·iowait), 호스트 메모리, 컨테이너 CPU·메모리(전체 합계 + 선택한 컨테이너). 구간 5·10·30·60분 |
| 디스크 | 마운트별 사용률 막대(75% 주황, 90% 빨강) |
| 상태줄 | 호스트 CPU·메모리·load·코어 수, 실행/전체 컨테이너 수, 조회 시간, 맺은 SSH 연결 수 |
| 우클릭·더블클릭 | 로그 보기(최근 200줄), start·stop·restart(stop·restart는 확인). Ctrl·Shift로 여러 줄을 고르면 한 번에, compose 컨테이너는 **프로젝트 전체**(예: penpot 7개)를 한 번에 조작. compose **의존 순서**를 지킴(시작은 DB 먼저·healthy 대기, 중지는 역순). 여러 개도 SSH 채널 하나에서 차례로 실행 |

---

## 동작 방식

```
Folderss(플러그인) ──SSH 연결 1개 유지(SSH.NET)──▶ 서버
   │  갱신마다 exec 채널 1개
   ▼
   curl --unix-socket /var/run/docker.sock  → Docker Engine API (컨테이너 목록, stats one-shot)
   /proc/stat · /proc/meminfo · /proc/loadavg · nproc → 호스트 CPU·메모리·부하
   df                                         → 디스크
```

- **연결 재사용**: 창마다 SSH 연결 하나를 열어 두고, 갱신·로그·컨테이너 조작은 그 연결에서 채널만 새로 엽니다. 끊기면 다음 갱신 때 다시 연결합니다. 창을 닫으면 연결도 끊습니다.
- **Docker API**: 텍스트 출력을 해석하는 `docker ps`/`docker stats` 대신 API JSON을 씁니다. `stats?one-shot=true`는 기다림이 없어서 컨테이너 10여 개 기준 한 번 조회가 0.3초 안팎입니다(예전 `docker stats --no-stream`은 약 2초).
- **CPU %**: 직전 갱신과의 누적값 차이로 계산합니다. 그래서 연결 직후 첫 갱신에는 CPU가 "측정 중"으로 나오고 두 번째부터 값이 나옵니다.
  - 컨테이너 CPU %는 **호스트 전체 = 100%** 기준입니다. `docker stats`는 코어 1개를 100%로 세서 4코어 서버면 400%까지 나오는데, 이 플러그인은 그 값을 코어 수로 나눠 보여 주고 사용 코어 수를 괄호로 붙입니다.
- 로그는 API 응답이 바이너리 형식이라 `docker logs` 명령으로 받습니다.

## 요구 사항

| 항목 | 내용 |
|---|---|
| 접속 | **키 인증**(암호 없는 개인키). `~/.ssh/config` 의 `Host` 별칭을 쓰면 `HostName`·`User`·`Port`·`IdentityFile`을 읽습니다. 별칭이 없으면 입력값을 호스트로, 기본 키(`id_ed25519`·`id_ecdsa`·`id_rsa`)를 씁니다 |
| 서버 키 | `~/.ssh/known_hosts`에 있어야 합니다(평문·해시 형식 모두). 없으면 연결을 거부하니 먼저 터미널에서 `ssh <별칭>`으로 한 번 접속해 두세요 |
| 서버 | Linux, Docker 25 이상(API 1.44), `curl`, 접속 계정이 `docker` 그룹(소켓 쓰기 권한) |

지원하지 않는 것: 암호 인증, 암호 걸린 개인키, Windows ssh-agent, `~/.ssh/config`의 `Match`·`Include`·`ProxyJump`.

## 호스트 설정

상단 콤보박스에서 등록한 서버를 고르면 바로 연결합니다. 서버 등록은 **[호스트 설정…]** 팝업에서 합니다.

![호스트 설정 팝업](docs/images/host-settings.png)

| 항목 | 내용 |
|---|---|
| 이름 | 콤보박스에 보이는 이름(겹치면 저장 안 됨) |
| 호스트 | `~/.ssh/config`의 `Host` 별칭 또는 IP·도메인 |
| 포트·사용자·개인키 | 비우면 별칭 설정(없으면 22, Windows 사용자 이름, 기본 키)을 쓰고, 채우면 그 값이 우선 |
| ~/.ssh/config 가져오기 | 와일드카드가 아닌 `Host` 별칭을 한 번에 추가 |
| 연결 시험 | 접속 후 `docker.sock` 접근과 Docker 버전까지 확인 |

호스트·사용자에는 영문·숫자·`. _ -`만 받습니다. 목록(`hosts`), 마지막 선택(`selectedHost`), 갱신 주기(`interval`)는 플러그인 설정(`%LOCALAPPDATA%\Folderss\plugin-data\zaruous.podman-monitor\settings.json`)에 저장합니다. 예전 버전의 단일 호스트 값(`host`)은 처음 열 때 목록의 첫 항목으로 옮겨집니다.

---

## 개발

```
src/PodmanMonitor/
├── PodmanMonitorPlugin.cs   ← 진입점 (IFolderssPlugin)
├── plugin.json
├── Ssh/                     ← ~/.ssh/config·known_hosts 해석, 연결 유지(SshConnection)
├── Monitoring/              ← 원격 스크립트·출력 해석(RemoteScript), 차이 계산·이력(Collector)
└── Ui/                      ← 화면(MonitorView), 그래프(TrendChart), 로그 창
tests/PodmanMonitor.Tests/   ← 단위 시험 (설정·known_hosts·파싱·CPU 계산)
tests/PodmanMonitor.UiTests/ ← 화면 시험 (Folderss 없이 zip을 불러 띄움)
```

```powershell
dotnet build src/PodmanMonitor          # → src/PodmanMonitor/bin/Debug/net8.0-windows/PodmanMonitor.zip
dotnet test tests/PodmanMonitor.Tests   # 단위 시험
pwsh scripts/ui-test.ps1                # 화면 시험 (숨은 데스크톱)

# 실제 서버에 붙는 화면 시험까지 (연결 1회 재사용, 컨테이너·디스크 표시 확인)
$env:PODMAN_MONITOR_LIVE_HOST = '<ssh 별칭>'; pwsh scripts/ui-test.ps1
```

zip에는 `PodmanMonitor.dll`, `PodmanMonitor.deps.json`, SSH.NET과 그 의존성 DLL이 들어갑니다. Folderss는 `.deps.json`으로 플러그인 폴더의 DLL을 찾습니다. 계약 DLL(`Folderss.PluginContract`)은 넣지 않습니다.

Folderss에 등록하기: `⋯ 메뉴 > 설정 > 플러그인 > 플러그인 찾기…`에서 zip을 고르고, **Folderss를 재시작**합니다.

이 저장소는 [FolderssPLuginTemplate](https://github.com/zaruous/FolderssPLuginTemplate)에서 시작했습니다. 시험 호스트 사용법, 테마 리소스 키, 플러그인 규칙(같은 프로세스에서 실행되므로 백그라운드 예외 금지 등)은 템플릿 README를 보세요.

## 빌드와 릴리스 (GitHub Actions)

`.github/workflows/build.yml`

- `main` 푸시·PR: 빌드해서 플러그인 zip을 Actions 아티팩트로 올립니다.
- `v*` 태그 푸시(예: `v0.2.0`): 태그 버전과 `plugin.json`의 `version`이 같은지 확인한 뒤 GitHub 릴리스에 zip을 첨부합니다.

## 라이선스

[MIT](LICENSE)
