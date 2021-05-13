﻿module CloudStorage.Server.Config

let private env = System.Environment.GetEnvironmentVariables()

let Oss = {|
    Endpoint = env.["OSSENDPOINT"] :?> string
    AccessKeyId = env.["OSSACCESSKEYID"] :?> string
    AccessKeySecret = env.["OSSACCESSKEYSECRET"] :?> string
    Bucket = "fcirno-test"
|}
let Security = {|
    Salt = env.["SALT"] :?> string
    Tokensalt = env.["TOKENSALT"] :?> string
|}
let Data = {|
    datasource = "Server=localhost;Database=test;User=root;Password=root"
|}
let Redis = {|
    Host = "localhost"
    Pass = "root"
|}
let Rabbit = {|
    AsyncTransferEnable = true
    RabbitURL = "amqp://guest:guest@127.0.0.1:5672/"
    TransExchangeName = "uploadserver.trans"
    TransOssQueueName = "uploadserver.trans.oss"
    TransOssErrQueueName = "uploadserver.trans.oss.err"
    TransOssRoutingKey = "oss"
|}