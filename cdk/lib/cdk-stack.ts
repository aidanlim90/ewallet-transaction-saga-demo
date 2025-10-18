import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as dotnet from '@aws-cdk/aws-lambda-dotnet';
import * as tasks from 'aws-cdk-lib/aws-stepfunctions-tasks';
import * as sfn from "aws-cdk-lib/aws-stepfunctions";
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as pipes from 'aws-cdk-lib/aws-pipes';
import * as iam from "aws-cdk-lib/aws-iam";
import * as kms from "aws-cdk-lib/aws-kms";

export class CdkStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const vpc = new ec2.Vpc(this, "TestVpc", {
      ipAddresses: ec2.IpAddresses.cidr('10.1.0.0/16'),
      maxAzs: 2,
      natGateways: 1,
    });

    const rdsSecurityGroup = new ec2.SecurityGroup(this, "RdsSecurityGroup", {
      vpc,
      description: "Allow PostgreSQL access from anywhere (for testing only)",
      allowAllOutbound: true,
    });

    rdsSecurityGroup.addIngressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(5432),
      "Allow PostgreSQL access from anywhere"
    );

        // Create a secret for DB credentials
    const dbSecret = new secretsmanager.Secret(this, 'DbSecret', {
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ username: 'testuser' }),
        generateStringKey: 'password',
        excludePunctuation: true,
        includeSpace: false,
      },
    });

    // RDS instance
    const db = new rds.DatabaseInstance(this, 'EwalletDb', {
      engine: rds.DatabaseInstanceEngine.postgres({ version: rds.PostgresEngineVersion.VER_16 }),
      instanceType: ec2.InstanceType.of(ec2.InstanceClass.T4G, ec2.InstanceSize.MICRO), // cheapest instance
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      credentials: rds.Credentials.fromSecret(dbSecret),
      allocatedStorage: 20,
      maxAllocatedStorage: 50,
      multiAz: false,
      securityGroups: [rdsSecurityGroup],
      publiclyAccessible: true, // for local testing
      removalPolicy: cdk.RemovalPolicy.DESTROY,
      deletionProtection: false,
    });

    new cdk.CfnOutput(this, 'DBEndpoint', {
      value: db.dbInstanceEndpointAddress,
    });

    new cdk.CfnOutput(this, 'DBSecretArn', {
      value: dbSecret.secretArn,
    });
    
    const username = dbSecret.secretValueFromJson('username').unsafeUnwrap();
    const password = dbSecret.secretValueFromJson('password').unsafeUnwrap();
    const dbConnectionString = `Host=${db.dbInstanceEndpointAddress};Database=EwalletDb;Username=${username};Password=${password};Port=${db.dbInstanceEndpointPort}`;
   
    const ewalletSingleTable = new dynamodb.Table(this, 'Ewallet', {
      partitionKey: { name: 'PK', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'SK', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PROVISIONED,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
      stream: dynamodb.StreamViewType.NEW_IMAGE, // enable stream
    });

    vpc.addGatewayEndpoint('DynamoDbEndpoint', {
      service: ec2.GatewayVpcEndpointAwsService.DYNAMODB,
    });

    new cdk.CfnOutput(this, 'EwalletSingleTableName', {
      value: ewalletSingleTable.tableName,
    });

    const createTransactionFunction = new dotnet.DotNetFunction(this, 'CreateTransactionFunction', {
      projectDir: '../src/Ewallet.CreateTransactionFunction',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: { msbuildParameters: ['/p:PublishAot=true'] },
      vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
      environment: {
        EWALLET_TABLE: ewalletSingleTable.tableName,
      },
    });

    ewalletSingleTable.grantWriteData(createTransactionFunction);

    const debitSenderWalletBalanceFunction = new dotnet.DotNetFunction(this, 'DebitSenderWalletBalanceFunction', {
      projectDir: '../src/Ewallet.DebitSenderWalletBalanceFunction',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: {
        msbuildParameters: ['/p:PublishAot=true'],
      },
      vpc: vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
      environment: {
        DB_CONNECTION_STRING: dbConnectionString,
      },
    });

    const debitSenderWalletBalanceFunctionArn = lambda.Function.fromFunctionArn(
      this,
      'DebitSenderWalletBalanceFunctionArn',
      debitSenderWalletBalanceFunction.functionArn
    );

    const failLambda = new dotnet.DotNetFunction(this, 'FailFunction', {
      projectDir: '../src/Fail',
      runtime: lambda.Runtime.PROVIDED_AL2023,
      bundling: {
        msbuildParameters: ['/p:PublishAot=true'],
      },
      vpc: vpc,
      architecture: lambda.Architecture.X86_64,
      memorySize: 512,
    });

    const failLambdaReference = lambda.Function.fromFunctionArn(
      this,
      'FailLambda',
      failLambda.functionArn
    );

    const failTask = new tasks.LambdaInvoke(this, 'Run Fail Lambda', {
      lambdaFunction: failLambdaReference,
      outputPath: '$.Payload',
    });

    const debitSenderWalletBalanceTask = new tasks.LambdaInvoke(this, 'Debit Sender Wallet Balance Task', {
      lambdaFunction: debitSenderWalletBalanceFunctionArn,
      inputPath: '$[0]',
      outputPath: '$.Payload',
    })
    .addRetry({
      errors: ['AccountNotFoundException', 'DuplicateTransactionException', 'InsufficientBalanceException', 'InvalidOperationException', 'ArgumentException'],
      maxAttempts: 0,
    })
    .addRetry({
      maxAttempts: 3,
      backoffRate: 2.0,
      interval: cdk.Duration.seconds(2),
      errors: ['States.ALL'],
    })
    .addCatch(failTask, {
      resultPath: '$.error',
    });
    
    const definition = debitSenderWalletBalanceTask;

    const transactionSaga =new sfn.StateMachine(this, "EwalletTransactionSagaOrchestration", {
      definitionBody: sfn.DefinitionBody.fromChainable(definition),
      timeout: cdk.Duration.minutes(5),
    });

    const pipeRole = new iam.Role(this, "DdbStreamPipeRole", {
      assumedBy: new iam.ServicePrincipal("pipes.amazonaws.com"),
    });

    // Allow reading the stream
    pipeRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:DescribeStream", "dynamodb:GetRecords", "dynamodb:GetShardIterator", "dynamodb:ListStreams"],
        resources: [ewalletSingleTable.tableStreamArn!],
      })
    );

    // Allow starting Step Function execution
    pipeRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["states:StartExecution", "states:StartSyncExecution"],
        resources: [transactionSaga.stateMachineArn],
      })
    );

    // Also allow logs if required (CloudWatch)
    pipeRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["logs:CreateLogStream", "logs:CreateLogGroup", "logs:PutLogEvents"],
        resources: ["*"],
      })
    );

    // --------------------------
    // Create the Pipe (CfnPipe) connecting the DynamoDB Stream -> StepFunction
    // --------------------------
    // Note: use PascalCase CFN keys inside the nested SourceParameters/TargetParameters object.
    new pipes.CfnPipe(this, "DdbStreamToStepFnPipe", {
      name: `${this.stackName}-DdbStreamToStepFnPipe`,
      roleArn: pipeRole.roleArn,
      source: ewalletSingleTable.tableStreamArn!,
      sourceParameters: {
        // CFN expects DynamoDBStreamParameters (case-sensitive)
        dynamoDbStreamParameters: {
          startingPosition: "LATEST",
          batchSize: 1
        },
        filterCriteria: {
          filters: [
          {
            pattern: JSON.stringify({
              eventName: ['INSERT'],
              "dynamodb": {
                "NewImage": {
                  "PK": { "S": [{ "prefix": "TRANSACTION#" }] }
                }
              }
            })
          }
      ]
    }
      },
      target: transactionSaga.stateMachineArn,
      targetParameters: {
        // CFN expects StepFunctionStateMachineParameters (case-sensitive)
        stepFunctionStateMachineParameters: {
          invocationType: "FIRE_AND_FORGET"
        },
        inputTemplate: JSON.stringify({
          TransactionId: "<$.dynamodb.NewImage.PK.S>",
          SenderUserId: "<$.dynamodb.NewImage.SenderId.S>",
          ReceiverUserId: "<$.dynamodb.NewImage.ReceiverId.S>",
          Amount: "<$.dynamodb.NewImage.Amount.N>"
        })
      }
    });
  }
}
