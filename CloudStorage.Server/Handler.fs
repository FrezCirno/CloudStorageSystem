﻿module CloudStorage.Server.Handler

open System
open System.IO
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open System.Text
open CloudStorage.Common
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Http
open Giraffe
open Microsoft.IdentityModel.JsonWebTokens
open Microsoft.IdentityModel.Tokens
open StackExchange.Redis
open Microsoft.AspNetCore.Authentication
open System.Threading

if not (Directory.Exists Config.TEMP_FILE_PATH) then
    Directory.CreateDirectory Config.TEMP_FILE_PATH
    |> ignore

let ArgumentError (err: string) = RequestErrors.BAD_REQUEST err

let jsonResp (code: int) (msg: string) (obj: obj) =
    if obj = null then
        json <| Utils.ResponseBrief code msg
    else
        json <| Utils.Response code msg obj

let okResp (msg: string) (obj: obj) = jsonResp 0 msg obj

///
/// Authentication
///
/// 刷新用户token
let UserUpdateToken (user_name: string) (user_token: string) : bool =
    Redis.redis.StringSet(RedisKey(user_name), RedisValue(user_token), TimeSpan.FromHours(1.0))

let UserValidToken (user_name: string) (user_token: string) : bool =
    Redis.redis.StringGet(RedisKey(user_name)) = RedisValue(user_token)

let EncryptPasswd =
    Utils.flip (+) Config.Security.Salt
    >> Utils.StringSha1

let notLoggedIn : HttpHandler =
    RequestErrors.UNAUTHORIZED "Cookie" "SAFE Realm" "You must be logged in."

let jwtAuthorized : HttpHandler =
    requiresAuthentication (challenge JwtBearerDefaults.AuthenticationScheme)

let cookieAuthorized : HttpHandler =
    requiresAuthentication (challenge CookieAuthenticationDefaults.AuthenticationScheme)

///
/// Storage Backend
///
let private SaveFile (fileHash: string) (fileName: string) (fileLength: int64) (stream: Stream) =
    if Database.File.FileHashExists fileHash then
        true
    else
        let tempPath =
            Path.Join [| Config.TEMP_FILE_PATH
                         fileHash |]

        let os = File.OpenWrite tempPath
        stream.CopyTo os
        os.Dispose()

        /// 发布消息
        let msg =
            RabbitMsg(
                FileHash = fileHash,
                CurLocation = tempPath,
                DstLocation = fileHash,
                DstType = RabbitMsg.Types.DstType.Minio
            )

        RabbitMq.Publish Config.Rabbit.TransExchangeName Config.Rabbit.TransRoutingKey msg
        Database.File.CreateFileMeta fileHash fileName fileLength tempPath

///
/// 获取文件，需要手动 Dispose
///
let private LoadFile (fileLoc: string) : Stream =
    if fileLoc.StartsWith Config.TEMP_FILE_PATH then
        upcast File.OpenRead fileLoc
    else
        /// 文件已转移到minio
        MinioOss.getObject fileLoc

module Upload =
    /// 用户上传文件
    let FileUploadHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        if ctx.Request.Form.Files.Count = 1 then
            let file = ctx.Request.Form.Files.[0]

            let stream = file.OpenReadStream()
            let fileHash = Utils.StreamSha1 stream

            let saveResult =
                stream.Seek(0L, SeekOrigin.Begin) |> ignore
                SaveFile fileHash file.Name file.Length stream

            if saveResult then
                if Database.UserFile.CreateUserFile username fileHash file.FileName file.Length then
                    okResp "OK" null next ctx
                else
                    ServerErrors.SERVICE_UNAVAILABLE "CreateUserFile" next ctx
            else
                ServerErrors.SERVICE_UNAVAILABLE "saveResult" next ctx
        else
            ArgumentError "File Count Exceed!" next ctx

    /// 文件元数据查询接口
    let FileMetaHandler (next: HttpFunc) (ctx: HttpContext) =
        match ctx.GetQueryStringValue "fileName" with
        | Error msg -> ArgumentError msg next ctx
        | Ok fileName ->
            match Database.File.GetFileMetaByFileName fileName with
            | None -> RequestErrors.notFound id next ctx
            | Some fileMeta -> okResp "OK" fileMeta next ctx

    [<CLIMutable>]
    type RecentFileBlock = { page: int; limit: int }

    /// 最近上传文件查询接口
    let RecentFileHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        match ctx.TryBindQueryString<RecentFileBlock>() with
        | Error msg -> ArgumentError msg next ctx
        | Ok args ->
            let result =
                Database.UserFile.GetUserFiles username args.page args.limit

            okResp "OK" result next ctx

    /// 用户文件查询接口
    let UserFileQueryHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        let limit =
            ctx.TryGetQueryStringValue "limit"
            |> Option.defaultValue "5"
            |> Int32.Parse

        let result =
            Database.UserFile.GetUserFiles username limit

        okResp "OK" result next ctx

    /// 文件下载接口
    /// 用户登录之后根据 filename 下载文件
    let FileDownloadHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        match ctx.GetQueryStringValue "filename" with
        | Error msg -> ArgumentError msg next ctx
        | Ok fileName ->
            /// 查询用户文件记录
            match Database.UserFile.GetUserFileByFileName username fileName with
            | None -> RequestErrors.NOT_FOUND "File Not Found" next ctx
            | Some userFile ->
                match Database.File.GetFileMetaByHash userFile.FileHash with
                | None -> ServerErrors.INTERNAL_ERROR "Sorry, this file is missing" next ctx
                | Some fileMeta ->
                    use data = LoadFile fileMeta.FileLoc
                    streamData true data None None next ctx


    /// 文件更新接口
    /// 用户登录之后通过此接口修改文件元信息
    let FileUpdateHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        let fileName =
            ctx.GetFormValue "filename"
            |> Option.defaultValue ""

        let op =
            ctx.GetFormValue "op" |> Option.defaultValue ""

        if op = "rename" then
            match ctx.GetFormValue "newName" with
            | None -> ArgumentError "new name is needed" next ctx
            | Some newName ->
                if
                    newName.Length <> 0
                    && not (Database.UserFile.IsUserHaveFile username newName)
                then
                    match Database.UserFile.GetUserFileByFileName username fileName with
                    | None -> RequestErrors.NOT_FOUND "File Not Found" next ctx
                    | Some userFile ->
                        let newUserFile = { userFile with FileName = newName }

                        if Database.UserFile.UpdateUserFileByUserFile username fileName newUserFile then
                            json newUserFile next ctx
                        else
                            ServerErrors.SERVICE_UNAVAILABLE id next ctx
                else
                    ArgumentError "invalid new name" next ctx
        else
            ServerErrors.NOT_IMPLEMENTED id next ctx

    /// 文件删除接口
    /// 用户登录之后根据 filename 删除文件
    let FileDeleteHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        match ctx.GetQueryStringValue "fileName" with
        | Error msg -> ArgumentError msg next ctx
        | Ok fileName ->
            /// 查询用户文件
            if not (Database.UserFile.IsUserHaveFile username fileName) then
                RequestErrors.NOT_FOUND "File Not Found" next ctx
            else
            /// 删除用户与文件之间的关联
            if Database.UserFile.DeleteUserFileByFileName username fileName then
                okResp "OK" null next ctx
            else
                ServerErrors.SERVICE_UNAVAILABLE id next ctx

module User =
    [<CLIMutable>]
    type UserRegisterBlock = { username: string; password: string }

    /// 用户注册接口
    let UserRegister (next: HttpFunc) (ctx: HttpContext) =
        task {
            match! ctx.TryBindFormAsync<UserRegisterBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok args ->
                if args.username.Length < 3
                   || args.password.Length < 5 then
                    return! ArgumentError "Invalid parameter" next ctx
                else
                    let enc_password = EncryptPasswd args.password

                    if Database.User.UserRegister args.username enc_password then
                        return! okResp "OK" null next ctx
                    else
                        return! ServerErrors.serviceUnavailable id next ctx
        }

    let BuildToken (username: string) =
        let tokenHandler = JwtSecurityTokenHandler()
        let tokenDescriptor = SecurityTokenDescriptor()

        tokenDescriptor.Subject <-
            ClaimsIdentity(
                [| Claim(JwtRegisteredClaimNames.Aud, "api")
                   Claim(JwtRegisteredClaimNames.Iss, "http://7c00h.xyz/cloud")
                   Claim(ClaimTypes.Name, username) |],
                JwtBearerDefaults.AuthenticationScheme
            )

        tokenDescriptor.Expires <- DateTime.UtcNow.AddHours(1.0)

        tokenDescriptor.SigningCredentials <-
            SigningCredentials(
                SymmetricSecurityKey(Encoding.ASCII.GetBytes Config.Security.Secret),
                SecurityAlgorithms.HmacSha256Signature
            )

        let securityToken = tokenHandler.CreateToken tokenDescriptor
        let writeToken = tokenHandler.WriteToken securityToken
        writeToken

    [<CLIMutable>]
    type UserLoginBlock = { username: string; password: string }

    /// 用户登录接口
    let UserLogin (next: HttpFunc) (ctx: HttpContext) =
        task {
            match! ctx.TryBindFormAsync<UserLoginBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok args ->
                let enc_password = EncryptPasswd args.password

                if Database.User.GetUserByUsernameAndUserPwd args.username enc_password then
                    let token = BuildToken(args.username)

                    if UserUpdateToken args.username token then
                        let ret =
                            {| FileLoc =
                                   ctx.Request.Scheme
                                   + "://"
                                   + ctx.Request.Host.Value
                                   + "/"
                               Username = args.username
                               AccessToken = token |}

                        return! okResp "OK" ret next ctx
                    else
                        return! ServerErrors.SERVICE_UNAVAILABLE "SERVICE_UNAVAILABLE" next ctx
                else
                    return! RequestErrors.FORBIDDEN "Wrong password" next ctx
        }

    /// 用户注销接口
    let UserLogout (next: HttpFunc) (ctx: HttpContext) =
        task {
            do! ctx.SignOutAsync()
            return! redirectTo false "/" next ctx
        }

    /// 用户信息查询接口
    let UserInfoHandler (next: HttpFunc) (ctx: HttpContext) =
        let username = ctx.User.FindFirstValue ClaimTypes.Name

        match Database.User.GetUserByUsername username with
        | None -> ServerErrors.INTERNAL_ERROR "User not found" next ctx
        | Some user -> okResp "OK" user next ctx

module MpUpload =
    [<CLIMutable>]
    type FastUploadInitBlock =
        { fileHash: string
          fileName: string
          fileSize: int64 }

    ///
    /// 尝试秒传
    ///
    let TryFastUploadHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match! ctx.TryBindFormAsync<FastUploadInitBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok initBlock ->
                /// 查询文件是否存在
                if not (Database.File.FileHashExists initBlock.fileHash) then
                    return! jsonResp -1 "秒传失败，请访问普通上传接口" null next ctx
                /// 存在则尝试秒传
                else if Database.UserFile.CreateUserFile
                            username
                            initBlock.fileHash
                            initBlock.fileName
                            initBlock.fileSize then
                    return! okResp "OK" null next ctx
                else
                    return! ServerErrors.SERVICE_UNAVAILABLE "秒传服务暂不可用，请访问普通上传接口" next ctx
        }

    [<CLIMutable>]
    type MultipartUploadBlock = { fileHash: string; fileSize: int64 }

    [<CLIMutable>]
    type MultipartInfo =
        { UploadKey: string
          ChunkSize: int
          ChunkCount: int
          ChunkExists: int [] }

    ///
    /// 初始化文件分块上传接口
    ///
    let InitMultipartUploadHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match! ctx.TryBindFormAsync<MultipartUploadBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok args ->
                let hashKey =
                    RedisKey(Config.HASH_KEY_PREFIX + args.fileHash)

                let uploadKey =
                    /// 如果 HASH_KEY_PREFIX + hash -> uploadKey 存在，说明是断点续传
                    match Redis.redis.KeyExists hashKey with
                    /// 不存在则创建一个新的
                    | false -> Utils.StringSha1(username + args.fileHash + Guid().ToString())
                    | true -> Redis.redis.StringGet(hashKey).ToString()

                /// 存在则获取chunk info
                let chunks =
                    Redis.redis.ListRange(RedisKey(Config.CHUNK_KEY_PREFIX + uploadKey))
                    |> Array.map int

                let ret =
                    { UploadKey = uploadKey
                      ChunkSize = Config.CHUNK_SIZE
                      ChunkCount =
                          float args.fileSize / float Config.CHUNK_SIZE
                          |> ceil
                          |> int
                      ChunkExists = chunks }

                ///
                /// 如果Redis中不存在uploadId Key, 说明是第一次上传
                /// 初始化断点信息到redis
                ///
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + ret.UploadKey)

                if not (Redis.redis.KeyExists mpKey) then

                    Redis.redis.HashSet(mpKey, RedisValue("username"), RedisValue(username))
                    |> ignore

                    Redis.redis.HashSet(mpKey, RedisValue("chunkCount"), RedisValue(string ret.ChunkCount))
                    |> ignore

                    Redis.redis.HashSet(mpKey, RedisValue("fileHash"), RedisValue(args.fileHash))
                    |> ignore

                    Redis.redis.HashSet(mpKey, RedisValue("fileSize"), RedisValue(string args.fileSize))
                    |> ignore

                    Redis.redis.KeyExpire(mpKey, TimeSpan.FromHours(8.0))
                    |> ignore

                    Redis.redis.StringSet(hashKey, RedisValue(ret.UploadKey), TimeSpan.FromHours(12.0))
                    |> ignore

                return! okResp "OK" ret next ctx
        }

    [<CLIMutable>]
    type UploadPartBlock = { uploadKey: string; index: int }

    ///
    /// 上传一个分片
    ///
    let UploadPartHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match ctx.TryBindQueryString<UploadPartBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok partInfo ->
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + partInfo.uploadKey)

                /// 检查 uploadKey 是否存在
                if not (Redis.redis.KeyExists mpKey) then
                    return! RequestErrors.notFound id next ctx
                else
                    /// 检查用户是否正确
                    let realuser =
                        Redis.redis.HashGet(mpKey, RedisValue("username"))

                    if realuser.ToString() <> username then
                        return! RequestErrors.FORBIDDEN "Not your file!" next ctx
                    else
                        let data = ctx.Request.Body

                        /// 将分片保存到 TEMP/upId/index
                        let fPath =
                            Path.Join [| Config.TEMP_FILE_PATH
                                         partInfo.uploadKey
                                         string partInfo.index |]

                        Directory.GetParent(fPath).Create()
                        use chunk = File.Create fPath
                        do! data.CopyToAsync chunk

                        ///
                        /// 每一个分片完成就在 CHUNK_KEY_PREFIX + uploadKey -> [  ] 中添加一项 index
                        ///
                        let chunkKey =
                            RedisKey(Config.CHUNK_KEY_PREFIX + partInfo.uploadKey)

                        let isExist =
                            Redis.redis.ListRange chunkKey
                            |> Array.exists (fun v -> (int v) = partInfo.index)

                        if not isExist then
                            Redis.redis.ListRightPush(chunkKey, RedisValue(string partInfo.index))
                            |> ignore

                        return! okResp "OK" None next ctx
        }

    /// Merge all chunks and return a big stream
    let MergeParts (fPath: string) (chunkCount: int) =
        let stream = new MemoryStream()

        for index in [ 0 .. chunkCount - 1 ] do
            use file =
                File.OpenRead(Path.Join [| fPath; string index |])

            file.CopyTo stream

        stream.Seek(0L, SeekOrigin.Begin) |> ignore
        stream

    [<CLIMutable>]
    type CompletePartBlock =
        { uploadKey: string
          fileHash: string
          fileSize: int64
          fileName: string }

    let __CompleteUploadPart (username: string) (args: CompletePartBlock) =
        let totalCount =
            Redis.redis.HashGet(RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + args.uploadKey), RedisValue("chunkCount"))
            |> int

        let uploadCount =
            Redis.redis.ListLength(RedisKey(Config.CHUNK_KEY_PREFIX + args.uploadKey))
            |> int

        if totalCount <> uploadCount then
            jsonResp -2 "invalid request" null
        else

            let tempFolder =
                Path.Join [| Config.TEMP_FILE_PATH
                             args.uploadKey |]

            use mergeStream = MergeParts tempFolder totalCount

            if not (SaveFile args.fileHash args.fileName args.fileSize mergeStream) then
                ServerErrors.SERVICE_UNAVAILABLE "SaveFile"
            elif not (Database.UserFile.CreateUserFile username args.fileHash args.fileName args.fileSize) then
                ServerErrors.SERVICE_UNAVAILABLE "CreateUserFile"
            else
                /// 清理Redis
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + args.uploadKey)

                let chunkKey =
                    RedisKey(Config.CHUNK_KEY_PREFIX + args.uploadKey)

                let hashKey =
                    RedisKey(Config.HASH_KEY_PREFIX + args.fileHash)

                if Redis.redis.KeyExists mpKey then

                    Directory.Delete(tempFolder, true)
                    Redis.redis.KeyDelete(hashKey) |> ignore
                    Redis.redis.KeyDelete(mpKey) |> ignore
                    Redis.redis.KeyDelete(chunkKey) |> ignore

                okResp "OK" null

    ///
    /// 分片上传完成接口
    ///
    let CompleteUploadPartHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match! ctx.TryBindFormAsync<CompletePartBlock>() with
            | Error msg -> return! ArgumentError msg next ctx
            | Ok args ->
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + args.uploadKey)

                if not (Redis.redis.KeyExists mpKey) then
                    return! RequestErrors.notFound id next ctx
                else
                    /// 检查用户是否正确
                    let realuser =
                        Redis.redis.HashGet(mpKey, RedisValue("username"))

                    if realuser.ToString() <> username then
                        return! RequestErrors.FORBIDDEN "Not your file!" next ctx
                    else
                        /// 防止多次提交
                        let dupKey =
                            RedisKey(
                                Config.HASH_KEY_PREFIX
                                + args.fileHash
                                + "_processing"
                            )

                        if Redis.redis.KeyExists dupKey then
                            return! RequestErrors.tooManyRequests id next ctx
                        else
                            Redis.redis.StringSet(dupKey, RedisValue("1"))
                            |> ignore

                            let res = __CompleteUploadPart username args
                            Redis.redis.KeyDelete dupKey |> ignore
                            return! res next ctx
        }

    ///
    /// 取消分片上传接口
    ///
    let CancelUploadPartHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match ctx.GetFormValue "uploadKey" with
            | None -> return! ArgumentError "uploadKey" next ctx
            | Some uploadKey ->
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + uploadKey)

                /// 检查 uploadKey 是否存在
                if not (Redis.redis.KeyExists mpKey) then
                    return! RequestErrors.notFound id next ctx
                else

                    /// 检查用户是否正确
                    let realuser =
                        Redis.redis.HashGet(mpKey, RedisValue("username"))

                    if realuser.ToString() <> username then
                        return! RequestErrors.FORBIDDEN "Not your file!" next ctx
                    else
                        let fileHash =
                            Redis.redis.HashGet(mpKey, RedisValue("fileHash"))

                        let hashKey =
                            RedisKey(Config.HASH_KEY_PREFIX + fileHash.ToString())

                        let chunkKey =
                            RedisKey(Config.CHUNK_KEY_PREFIX + uploadKey)

                        let tempFolder =
                            Path.Join [| Config.TEMP_FILE_PATH
                                         uploadKey |]

                        Directory.Delete(tempFolder, true)
                        Redis.redis.KeyDelete(hashKey) |> ignore
                        Redis.redis.KeyDelete(mpKey) |> ignore
                        Redis.redis.KeyDelete(chunkKey) |> ignore

                        return! okResp "OK" "Success delete" next ctx
        }

    ///
    /// 查看分片上传状态接口
    ///
    let MultipartUploadStatusHandler (next: HttpFunc) (ctx: HttpContext) =
        task {
            let username = ctx.User.FindFirstValue ClaimTypes.Name

            match ctx.GetFormValue "uploadKey" with
            | None -> return! ArgumentError "uploadKey is needed" next ctx
            | Some uploadKey ->
                let mpKey =
                    RedisKey(Config.UPLOAD_INFO_KEY_PREFIX + uploadKey)

                if not (Redis.redis.KeyExists mpKey) then
                    return! RequestErrors.notFound id next ctx
                else
                    /// 检查用户是否正确
                    let realuser =
                        Redis.redis.HashGet(mpKey, RedisValue("username"))

                    if realuser.ToString() <> username then
                        return! RequestErrors.FORBIDDEN "Not your file!" next ctx
                    else
                        let chunks =
                            Redis.redis.ListRange(RedisKey(Config.CHUNK_KEY_PREFIX + uploadKey))
                            |> Array.map (fun x -> x.ToString() |> int)

                        let ret =
                            {| UploadKey = uploadKey
                               FileHash =
                                   Redis
                                       .redis
                                       .HashGet(mpKey, RedisValue("fileHash"))
                                       .ToString()
                               FileSize =
                                   Redis
                                       .redis
                                       .HashGet(mpKey, RedisValue("fileSize"))
                                       .ToString()
                                   |> int64
                               ChunkSize = Config.CHUNK_SIZE
                               ChunkCount =
                                   Redis
                                       .redis
                                       .HashGet(mpKey, RedisValue("chunkCount"))
                                       .ToString()
                                   |> int
                               ChunkExists = chunks |}

                        return! okResp "OK" ret next ctx
        }

let routes : HttpHandler =
    choose [ route "/ping" >=> Successful.OK "pong!"
             route "/time"
             >=> warbler (fun _ -> text (DateTime.Now.ToString()))

             route "/user/signup" >=> User.UserRegister
             route "/user/signin" >=> User.UserLogin
             route "/user/signout" >=> User.UserLogout
             route "/user/info"
             >=> jwtAuthorized
             >=> User.UserInfoHandler

             route "/file/upload"
             >=> choose [ POST
                          >=> jwtAuthorized
                          >=> Upload.FileUploadHandler
                          RequestErrors.METHOD_NOT_ALLOWED id ]
             route "/file/meta"
             >=> jwtAuthorized
             >=> Upload.FileMetaHandler
             route "/file/recent"
             >=> jwtAuthorized
             >=> Upload.RecentFileHandler
             route "/file/download"
             >=> jwtAuthorized
             >=> Upload.FileDownloadHandler
             route "/file/update"
             >=> jwtAuthorized
             >=> Upload.FileUpdateHandler
             route "/file/delete"
             >=> jwtAuthorized
             >=> Upload.FileDeleteHandler

             route "/file/fastupload"
             >=> jwtAuthorized
             >=> MpUpload.TryFastUploadHandler
             route "/file/mpupload/init"
             >=> jwtAuthorized
             >=> MpUpload.InitMultipartUploadHandler
             route "/file/mpupload/uppart"
             >=> jwtAuthorized
             >=> MpUpload.UploadPartHandler
             route "/file/mpupload/complete"
             >=> jwtAuthorized
             >=> MpUpload.CompleteUploadPartHandler
             route "/file/mpupload/cancel"
             >=> jwtAuthorized
             >=> MpUpload.CancelUploadPartHandler
             route "/file/mpupload/status"
             >=> jwtAuthorized
             >=> MpUpload.MultipartUploadStatusHandler

             RequestErrors.notFound (text "404 Not Found") ]
