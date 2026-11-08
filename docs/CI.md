# CI/CD: веб-версия

Каждый push в `master` (кроме изменений только в документации) запускает `.github/workflows/web.yml`:

1. **Тесты.** EditMode-тесты: физика, удары, судейство, счёт, матчи ИИ.
2. **Сборка WebGL.** Unity в Docker-образе GameCI вызывает `ProjectBuilder.BuildWebGL`, результат кладётся в `Build/WebGL`. Папка `Library` кэшируется между запусками.
3. **Деплой** на https://tennis.umolab.ru через `Tools/CI/deploy-web.sh`, затем проверка, что сайт отвечает.

Запустить вручную: GitHub → Actions → «Web build and deploy» → «Run workflow».

PlayMode-тесты в CI не запускаются: им нужна видеокарта. Их запускают локально.

## Секреты

GitHub → Settings → Secrets and variables → Actions.

| Секрет | Что это |
|---|---|
| `UNITY_EMAIL`, `UNITY_PASSWORD` | Аккаунт Unity, на который активирован редактор (Personal) |
| `UNITY_LICENSE` | Необязательно: содержимое файла `Unity_lic.ulf`, если активировать так |
| `DEPLOY_SSH_KEY` | Закрытый ключ для деплоя (см. ниже) |
| `DEPLOY_KNOWN_HOSTS` | Вывод `ssh-keyscan fp.cloud28.umolab.ru` |
| `DEPLOY_HOST` | `user1@fp.cloud28.umolab.ru` |

Пока секретов нет, сборка и деплой пропускаются с предупреждением, но не падают.

## Ключ для деплоя

У `user1` на сервере есть `sudo`, поэтому CI получает отдельный ключ, ограниченный командой `rrsync`. С ним можно только синхронизировать файлы в папку сайта: ни оболочки, ни других команд. Папка сайта (`/var/www/fastuser/data/www/tennis.umolab.ru`) доступна на запись группе `fastuser`, в которой состоит `user1`; владелец остаётся `fastuser`.

Создать ключ и разрешить его на сервере (Git Bash, из корня проекта):

```bash
ssh-keygen -t ed25519 -N "" -C tennis-ci-deploy -f tennis_deploy
ssh user1@fp.cloud28.umolab.ru "cat >> ~/.ssh/authorized_keys" <<EOF
restrict,command="/usr/bin/rrsync /var/www/fastuser/data/www/tennis.umolab.ru" $(cat tennis_deploy.pub)
EOF
gh secret set DEPLOY_SSH_KEY < tennis_deploy
ssh-keyscan fp.cloud28.umolab.ru | gh secret set DEPLOY_KNOWN_HOSTS
gh secret set DEPLOY_HOST --body "user1@fp.cloud28.umolab.ru"
rm tennis_deploy tennis_deploy.pub
```

## Ручной деплой

```bash
# Сборка: Unity → Tennis → Build WebGL, или из командной строки:
Unity -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod TennisSim.EditorTools.ProjectBuilder.BuildWebGL
# Выкладка своим ключом:
bash Tools/CI/deploy-web.sh
```

Сборка сжата gzip с распаковкой в браузере (`decompressionFallback`), поэтому настраивать заголовки на сервере не нужно.
