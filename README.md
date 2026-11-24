# Folderss Podman 모니터

[Folderss](https://github.com/zaruous/Folderss) 플러그인입니다. SSH로 접속한 서버의 **Podman 컨테이너 상태**와 **호스트 CPU·메모리·디스크**를 한 창에서 보여 줍니다.
서버에는 아무것도 설치하지 않습니다(podman만 있으면 됩니다).

`⋯ 메뉴 > 플러그인 > Podman 모니터`

[Folderss Docker 모니터](https://github.com/zaruous/Folderss-docker-monitor)와 화면·코드 구조가 같고, 서버에서 정보를 가져오는 방법만 Docker Engine API 대신 **podman CLI**입니다. 다른 점은 [Docker 모니터와 다른 점](#docker-모니터와-다른-점)에 모아 두었습니다.

| 영역 | 내용 |
|---|---|
| 상단 막대 | 호스트 선택, [호스트 설정…], 갱신 주기(2·3·5·10·30초, 기본 3초), 자동 갱신, 새로고침, 프로젝트별 묶기 |
| 알림 줄 | 컨테이너 멈춤·사라짐·다시 실행, unhealthy·회복, 디스크·호스트 메모리 90% 넘김을 직전 갱신과 비교해 알림. 창이 비활성이면 작업 표시줄 깜빡임. [기록]·[지우기]. 직접 조작한 컨테이너는 3분간 알리지 않음 |
| 컨테이너 표 | 이름·프로젝트/pod·상태·상세·CPU(호스트 전체 대비 %, 사용 코어 수)·메모리·네트워크·디스크 I/O·이미지·포트. 상태 색, unhealthy 줄 강조, CPU·메모리 80% 이상 강조. **compose 프로젝트별, compose가 아니면 pod별 묶음**(머리줄에 실행 수·CPU·메모리 합계, 눌러서 접기). pod의 infra 컨테이너는 보이지 않음 |
| 추이 그래프 | 호스트 CPU(사용률·iowait), 호스트 메모리, 컨테이너 CPU·메모리(전체 합계 + 선택한 컨테이너). 구간 5·10·30·60분 |
| 디스크 | 마운트별 사용률 막대(75% 주황, 90% 빨강) |
| 상태줄 | 호스트 CPU·메모리·load·코어 수, 실행/전체 컨테이너 수, 조회 시간, 맺은 SSH 연결 수. 통계를 못 읽으면 `⚠` 이유 |
| 우클릭·더블클릭 | 로그 보기(최근 200줄), start·stop·restart(stop·restart는 확인). Ctrl·Shift로 여러 줄을 고르면 한 번에, 같은 묶음이면 **프로젝트 전체·pod 전체**를 한 번에 조작. compose **의존 순서**를 지킴(시작은 DB 먼저·healthy 대기, 중지는 역순). 여러 개도 SSH 채널 하나에서 차례로 실행 |

---

## 동작 방식

```
Folderss(플러그인) ──SSH 연결 1개 유지(SSH.NET)──▶ 서버 (접속한 계정으로 실행)
   │  갱신마다 exec 채널 1개
   ▼
   podman ps -a --pod --format json            → 컨테이너 목록 (pod·compose 라벨 포함)
   podman stats --no-stream --format '{{json .}}' → 실행 중인 컨테이너의 누적 CPU 시간·메모리·네트워크·블록 I/O
   /proc/stat · /proc/meminfo · /proc/loadavg · nproc → 호스트 CPU·메모리·부하
   df                                          → 디스크
```

- **연결 재사용**: 창마다 SSH 연결 하나를 열어 두고, 갱신·로그·컨테이너 조작은 그 연결에서 채널만 새로 엽니다. 끊기면 다음 갱신 때 다시 연결합니다. 창을 닫으면 연결도 끊습니다.
- **데몬 없음**: Podman은 소켓·데몬이 없어도 되므로 `podman.socket`을 켤 필요가 없습니다. podman 표준 오류(rootless 경고 등)는 JSON과 섞이지 않게 따로 받습니다.
- **CPU %**: 직전 갱신과의 누적 CPU 시간 차이 ÷ 지난 시간으로 사용 코어 수를 구하고, 호스트 코어 수(`nproc`)로 나눠 **호스트 전체 = 100%** 기준으로 보여 줍니다(괄호 안은 사용 코어 수). 그래서 연결 직후 첫 갱신에는 CPU가 "측정 중"으로 나오고 두 번째부터 값이 나옵니다.
- **조작**: `podman start|stop|restart <id>`. 이미 그 상태여도 podman은 성공으로 끝납니다. 실패하면 상태줄에 종료 코드와 podman 오류 문구가 나옵니다.
- **healthy 대기**(compose `service_healthy` 의존): 상태를 읽기만 하지 않고 `podman healthcheck run`으로 검사를 직접 돌립니다(최대 90초). podman의 주기 검사는 systemd 타이머로 돌아서, systemd 사용자 세션이 없으면(rootless 계정에 linger가 꺼져 있는 등) 상태가 계속 `starting`에 머물기 때문입니다.

### Windows 서버

접속하면 먼저 `uname -s`를 실행해 봅니다. 되면 Linux(sh), 안 되면 Windows로 보고 같은 일을 하는 PowerShell 5.1 스크립트를 `powershell -EncodedCommand`로 보냅니다(cmd 한 줄 8191자 제한 안).

- 컨테이너: `podman.exe`가 podman machine(WSL VM)에 붙으므로 JSON은 Linux와 같습니다. VM이 꺼져 있으면 podman 오류가 상태줄에 나옵니다(`podman machine start`).
- 호스트 CPU: 성능 카운터(`Win32_PerfRawData_PerfOS_Processor`)의 사용자·커널·유휴 시간 차이. iowait·load average는 없습니다.
- 메모리·디스크: `Win32_OperatingSystem`, `Win32_LogicalDisk`(로컬 드라이브).
- 컨테이너 CPU %는 Windows 호스트 코어 수 기준입니다(VM에 준 코어 수가 아님).
- 여러 컨테이너를 한 번에 조작할 때 명령 길이 때문에 id는 앞 12자리만 씁니다. 너무 많으면(약 90단계 넘음) 나눠서 조작하라고 알립니다.

## 요구 사항

| 항목 | 내용 |
|---|---|
| 접속 | **키 인증**(암호 없는 개인키). `~/.ssh/config` 의 `Host` 별칭을 쓰면 `HostName`·`User`·`Port`·`IdentityFile`을 읽습니다. 별칭이 없으면 입력값을 호스트로, 기본 키(`id_ed25519`·`id_ecdsa`·`id_rsa`)를 씁니다 |
| 서버 키 | `~/.ssh/known_hosts`에 있어야 합니다(평문·해시 형식 모두). 없으면 연결을 거부하니 먼저 터미널에서 `ssh <별칭>`으로 한 번 접속해 두세요 |
| 서버 | **Linux**: Podman 4 이상(4.9.3에서 확인), 로그인 셸이 sh 호환(bash·dash 확인)<br>**Windows**: OpenSSH 서버 + Podman for Windows(podman machine, 6.2.0에서 확인). 로그인 셸은 cmd·PowerShell 어느 것이든 됩니다 |
| 통계 | rootless는 **cgroup v2**가 있어야 CPU·메모리 통계가 나옵니다(cgroup v1이면 목록·조작·호스트 지표만, 상태줄에 이유 표시). [연결 시험]이 rootless/rootful·cgroup 버전을 알려 줍니다 |

지원하지 않는 것: 암호 인증, 암호 걸린 개인키, Windows ssh-agent, `~/.ssh/config`의 `Match`·`Include`·`ProxyJump`, `sudo`(아래 참고).

### 어떤 컨테이너가 보이나

podman은 **SSH로 접속한 계정의 컨테이너만** 보여 줍니다.

- 일반 계정으로 접속 → 그 계정의 rootless 컨테이너
- root로 접속 → rootful 컨테이너
- root가 띄운 컨테이너를 일반 계정으로 보는 방법(`sudo podman`)은 지원하지 않습니다. 필요하면 root 계정으로 호스트를 하나 더 등록하세요.

## 호스트 설정

상단 콤보박스에서 등록한 서버를 고르면 바로 연결합니다. 서버 등록은 **[호스트 설정…]** 팝업에서 합니다.

| 항목 | 내용 |
|---|---|
| 이름 | 콤보박스에 보이는 이름(겹치면 저장 안 됨) |
| 호스트 | `~/.ssh/config`의 `Host` 별칭 또는 IP·도메인 |
| 포트·사용자·개인키 | 비우면 별칭 설정(없으면 22, Windows 사용자 이름, 기본 키)을 쓰고, 채우면 그 값이 우선 |
| ~/.ssh/config 가져오기 | 와일드카드가 아닌 `Host` 별칭을 한 번에 추가 |
| 연결 시험 | 접속 후 podman 실행과 버전·rootless 여부·cgroup 버전까지 확인 |

호스트·사용자에는 영문·숫자·`. _ -`만 받습니다. 목록(`hosts`), 마지막 선택(`selectedHost`), 갱신 주기(`interval`)는 플러그인 설정(`%LOCALAPPDATA%\Folderss\plugin-data\zaruous.podman-monitor\settings.json`)에 저장합니다. Docker 모니터와 플러그인 id가 달라서 설정은 따로 저장됩니다.

## 묶음(프로젝트/pod)

| 컨테이너 | 묶음 이름 | 묶음 전체 조작 | 의존 순서 |
|---|---|---|---|
| compose 라벨 있음(`com.docker.compose.project`, podman-compose의 `io.podman.compose.project`) | 프로젝트 이름 | `'이름' 프로젝트 전체 …` | `com.docker.compose.depends_on` 라벨이 있을 때만(docker compose가 붙임. podman-compose는 붙이지 않을 수 있음 — 그러면 순서 없이 조작) |
| compose 아님, pod에 속함 | `이름 (pod)` | `'이름' pod 전체 …` | 없음 |
| 둘 다 아님 | `(compose·pod 아님)` | 없음 | 없음 |

podman-compose는 보통 컨테이너를 pod에도 넣지만 compose 라벨이 우선이라 프로젝트로 묶입니다.

## Docker 모니터와 다른 점

| 항목 | Docker 모니터 | Podman 모니터 |
|---|---|---|
| 정보 얻는 법 | `curl --unix-socket /var/run/docker.sock` (Engine API) | `podman ps`·`podman stats` JSON (CLI, 데몬 없음) |
| 서버 요구 | Docker 25+, `curl`, `docker` 그룹 | Podman 4+, rootless면 cgroup v2 |
| 보이는 컨테이너 | 데몬의 전체 컨테이너 | 접속한 계정의 컨테이너 |
| 묶음 | compose 프로젝트 | compose 프로젝트, 없으면 pod |
| 조작 결과 | HTTP 204/304(이미 그 상태) | 종료 코드(이미 그 상태도 성공) |
| healthy 대기 | 상태 읽기 | `podman healthcheck run`으로 직접 검사 |
| 재시작 반복 알림 | `restarting` 상태로 감지 | podman에는 `restarting` 상태가 없어, 갱신 사이에 다시 올라오면 감지하지 못함 |

---

## 개발

```
src/PodmanMonitor/
├── PodmanMonitorPlugin.cs   ← 진입점 (IFolderssPlugin)
├── plugin.json
├── Ssh/                     ← ~/.ssh/config·known_hosts 해석, 연결 유지(SshConnection)
├── Monitoring/              ← 원격 스크립트·출력 해석(RemoteScript), 차이 계산·이력(Collector)
└── Ui/                      ← 화면(MonitorView), 그래프(TrendChart), 로그 창
tests/PodmanMonitor.Tests/   ← 단위 시험 (설정·known_hosts·podman 출력 파싱·CPU 계산)
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

이 저장소는 [Folderss-docker-monitor](https://github.com/zaruous/Folderss-docker-monitor)(그 자신은 [FolderssPLuginTemplate](https://github.com/zaruous/FolderssPLuginTemplate)에서 시작)를 복사해 podman용으로 바꿨습니다. 시험 호스트 사용법, 테마 리소스 키, 플러그인 규칙(같은 프로세스에서 실행되므로 백그라운드 예외 금지 등)은 템플릿 README를 보세요.

## 빌드와 릴리스 (GitHub Actions)

`.github/workflows/build.yml`

- `main` 푸시·PR: 빌드해서 플러그인 zip을 Actions 아티팩트로 올립니다.
- `v*` 태그 푸시(예: `v0.2.0`): 태그 버전과 `plugin.json`의 `version`이 같은지 확인한 뒤 GitHub 릴리스에 zip을 첨부합니다.

## 라이선스

[MIT](LICENSE)
