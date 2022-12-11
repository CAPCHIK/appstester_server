# AppsTester
Сервис для автоматической проверки заданий по android-разработке и выставлению оценок на платформе Moodle

## Технологии
* .NET 5.0
* Moodle
* Redis (опционально)
* RabbitMQ
* Sentry
* ADB
* Android SDK

## Инфраструктура
![image](https://user-images.githubusercontent.com/70891118/205965119-8ed9309b-3707-488a-8535-aff4935226a7.png)

```mermaid
sequenceDiagram
    autonumber
    actor Студент

    Студент ->> Moodle: Загрузка задания
    loop Раз в секунду
      Controller ->> Moodle: Запрос решений
    end
    note right of Controller: Есть актуальные решения
    Controller -) Очередь сообщений: Отправка запроса на проверку
    Очередь сообщений ->> +Android checker: Запрос на проверку
    Android checker ->> Controller: Запрос задания и решения
    Android checker ->> Android device (через ADB): Выполнение теста
    Android device (через ADB) ->> Android checker: Результат работы
    Android checker -> Android checker: Парсинг рзультата и формирование результата проверки
    Android checker -) -Очередь сообщений: Результат проверки
    Очередь сообщений ->> Controller: Результат проверки
    Controller ->> Moodle: Обновление результата решения

```
## Конфигурация
Для локальной разработки можно хранить конфигурацию в `/{Project}}/appsettings.Local.json`
#### AppsTester/Controller
* **ConnectionStrings__RabbitMq** - строка подключения к RabbitMQ. Пример: ```amqp://root:root@localhost:5672```
* **ConnectionStrings__DefaultConnection** - строка подключения к БД. Пример: ```Server=localhost;Port=5432;Database=appstester;User Id=postgres;Password=root;```
* **Moodle__BasicToken** - токен Moodle
* **Moodle_Url** - url для подключения к Moodle. Пример: ```http://localhost:6003```
* **Moodle__Token** - аналогичен **Moodle__BasicToken**
#### AppsTester/Checker/Android
* **ConnectionStrings__RabbitMq** - строка подключения к RabbitMQ. Пример: ```amqp://root:root@localhost:5672```
* **ConnectionStrings__DevicesSynchronizationRedis** - строка подключения к Redis для получения списка незарезервированных устройств.
* **Controller__Url** - строка подключения к **AppsTester/Controller**. Пример: ```http://localhost:5000```
* **Adb__Host** - хост ADB-сервера. Пример: ```localhost```
* **ANDROID_SDK_ROOT** - путь к Android SDK. Примеры: Linux - ```/home/ubuntu/android-sdk/```; Windows - ```C:\\Users\\PC\\AppData\\Local\\Android\\Sdk```
