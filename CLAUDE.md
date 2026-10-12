# CLAUDE.md

<!-- 이 프로젝트에만 해당하는 내용만 적는다. 코딩 지침, CHANGELOG 규칙, 멀티 에이전트 규칙, 스킬은 구글 드라이브 AgentSync의 전체 메모리에서 공통으로 불러온다. -->
<!-- 0_Startup을 복사해 새 프로젝트를 만들면 아래 칸을 채우고, 쓸 내용이 없는 칸은 지운다. -->

## 프로젝트 개요
<!-- 전시명, 설치 장소, 씬 흐름 -->
- 전시명: 전화부스
- 설치 장소: 천안 독립기념관
- 0_Startup 템플릿 기반. 진행 상황은 `인수인계.md` 맨 위 "지금 상태"를 먼저 읽는다.
- 씬: `0_Idle` 하나뿐이다(빌드 첫 씬). 화면 6개(Canvas 아래)와 `IdleLifetimeScope`·`PageMediaPlayer`·`IdleSceneController`(씬 매니저)가 있다. 씬마다 `{씬}SceneController` + `{씬}LifetimeScope` 구성을 따른다.

## 하드웨어·외부 연동
<!-- 센서, RFID, 시리얼, API 엔드포인트 -->

## 공통 규칙의 예외
<!-- 예: 씬 매니저는 GameManagerBase 대신 MonoBehaviour 유지 -->
- 씬 매니저(`{씬}SceneController`)는 `GameManagerBase` 대신 `MonoBehaviour`를 상속한다. `GameManagerBase`는 `SingletonGuard`로 하나만 남는 전역 매니저라 루트 프리팹의 `GameManager`가 이미 쓰고 있다. 씬 매니저는 씬의 `{씬}LifetimeScope`에서 `RegisterComponent`로 주입받는다.
- 스킬 `unity-stack-scaffold`, `unity-network-protocol`, `changelog`는 `C:\Users\HULIAC\Unity\claude-skills` 원본을 `.claude/skills/`에 복사해 이 프로젝트에서만 쓴다. 복사본이라 원본을 고치면 다시 복사해야 반영된다.
